using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Owns the only write connection to one day-file's SQLite database, on one dedicated
/// background thread — build plan §3.6: <c>SqliteConnection</c> is not thread-safe even
/// under a lock, so the connection is never exposed outside this class, and this is the
/// only code that ever touches it. Batches every <see cref="BatchRowLimit"/> rows or
/// <see cref="BatchTimeLimit"/>, whichever comes first, driven by <see cref="TimeProvider"/>
/// so a future fake-clock test can control the boundary precisely.
/// </summary>
public sealed class SqliteBatchWriter : IDisposable
{
    public const int BatchRowLimit = 500;
    public static readonly TimeSpan BatchTimeLimit = TimeSpan.FromMilliseconds(200);

    private readonly SqliteConnection _connection;
    private readonly SubjectInterner _subjects;
    private readonly TimeProvider _timeProvider;
    private readonly BlockingCollection<WriteOp> _queue = new(new ConcurrentQueue<WriteOp>());
    private readonly Thread _thread;

    /// <summary>Diagnostic only — number of ops enqueued but not yet committed. Used to
    /// confirm/refute the backlog theory behind PROGRESS.md's unexplained heap growth before
    /// any fix is made; kept afterward since it is generally useful for surfacing writer
    /// health (e.g. a future status-bar "write lag" indicator).</summary>
    public int PendingCount => _queue.Count;

    public SqliteBatchWriter(string databasePath, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        SqliteSchema.Apply(_connection);
        SqliteCapabilityProbe.Verify(_connection);
        _subjects = new SubjectInterner(_connection);

        _thread = new Thread(RunLoop) { IsBackground = true, Name = "EventScope-SqliteWriter" };
        _thread.Start();
    }

    /// <summary>Posts one op. Safe from any thread — only the caller enqueues; the writer
    /// thread is the only one that ever dequeues.</summary>
    public void Enqueue(WriteOp op)
    {
        if (!_queue.TryAdd(op))
        {
            throw new ObjectDisposedException(nameof(SqliteBatchWriter));
        }
    }

    /// <summary>Waits until every op enqueued before this call has committed — for tests
    /// that need to observe a specific write landed, without polling.</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new FlushBarrier(tcs));
        return tcs.Task;
    }

    private void RunLoop()
    {
        var pending = new List<WriteOp.InsertMessage>(BatchRowLimit);
        var barriers = new List<TaskCompletionSource>();

        try
        {
            while (true)
            {
                var deadline = _timeProvider.GetUtcNow() + BatchTimeLimit;

                while (pending.Count < BatchRowLimit)
                {
                    var remaining = deadline - _timeProvider.GetUtcNow();
                    if (remaining <= TimeSpan.Zero) break;
                    if (_queue.IsCompleted) break;

                    if (!_queue.TryTake(out var op, remaining)) break; // batch window elapsed

                    switch (op)
                    {
                        case WriteOp.InsertMessage insert:
                            pending.Add(insert);
                            break;
                        case FlushBarrier barrier:
                            barriers.Add(barrier.Completion);
                            break;
                    }
                }

                if (pending.Count > 0)
                {
                    CommitBatch(pending);
                    pending.Clear();
                }

                foreach (var barrier in barriers) barrier.TrySetResult();
                barriers.Clear();

                if (_queue.IsCompleted && _queue.Count == 0) break;
            }
        }
        finally
        {
            // Microsoft.Data.Sqlite pools the underlying native handle by default, so
            // Dispose() alone leaves the file locked on Windows — the same collision the
            // build plan §3.6 calls out for deleting a day file. ClearAllPools forces every
            // pooled handle closed (not just this connection's own pool entry — any other
            // short-lived connection opened against the same path, e.g. a search reader,
            // pools independently) so the file is actually free the moment this returns.
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private void CommitBatch(List<WriteOp.InsertMessage> rows)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages
                (enqueued_ticks, received_ticks, segment_id, offset, length,
                 message_id, correlation_id, subject_id, partition, flags, preview, body_head)
            VALUES
                ($enqueuedTicks, $receivedTicks, $segmentId, $offset, $length,
                 $messageId, $correlationId, $subjectId, $partition, $flags, $preview, $bodyHead)
            """;

        var pEnqueuedTicks = command.Parameters.Add("$enqueuedTicks", SqliteType.Integer);
        var pReceivedTicks = command.Parameters.Add("$receivedTicks", SqliteType.Integer);
        var pSegmentId = command.Parameters.Add("$segmentId", SqliteType.Integer);
        var pOffset = command.Parameters.Add("$offset", SqliteType.Integer);
        var pLength = command.Parameters.Add("$length", SqliteType.Integer);
        var pMessageId = command.Parameters.Add("$messageId", SqliteType.Text);
        var pCorrelationId = command.Parameters.Add("$correlationId", SqliteType.Text);
        var pSubjectId = command.Parameters.Add("$subjectId", SqliteType.Integer);
        var pPartition = command.Parameters.Add("$partition", SqliteType.Integer);
        var pFlags = command.Parameters.Add("$flags", SqliteType.Integer);
        var pPreview = command.Parameters.Add("$preview", SqliteType.Text);
        var pBodyHead = command.Parameters.Add("$bodyHead", SqliteType.Text);

        foreach (var row in rows)
        {
            pEnqueuedTicks.Value = row.EnqueuedTicks;
            pReceivedTicks.Value = row.ReceivedTicks;
            pSegmentId.Value = row.SegmentId;
            pOffset.Value = row.Offset;
            pLength.Value = row.Length;
            pMessageId.Value = (object?)row.MessageId ?? DBNull.Value;
            pCorrelationId.Value = (object?)row.CorrelationId ?? DBNull.Value;
            pSubjectId.Value = _subjects.Intern(row.Subject);
            pPartition.Value = (object?)row.Partition ?? DBNull.Value;
            pFlags.Value = row.Flags;
            pPreview.Value = (object?)row.Preview ?? DBNull.Value;
            pBodyHead.Value = (object?)row.BodyHead ?? DBNull.Value;

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private sealed record FlushBarrier(TaskCompletionSource Completion) : WriteOp;
}

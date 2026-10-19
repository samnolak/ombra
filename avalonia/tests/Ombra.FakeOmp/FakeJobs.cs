using System.Text;
using System.Text.Json.Nodes;

namespace Ombra.FakeOmp;

/// <summary>
/// Background jobs over RPC like the owned engine (rpc-jobs.ts): <c>get_jobs</c>, <c>cancel_job</c> and
/// <c>get_job_output</c> (a bounded UTF-8 byte cursor), in every scenario. <c>test_job_control</c> replaces the
/// jobs, appends output, changes a status or the "waiting for input" flag, drops old bytes, cans refusals, and
/// answers the job requests the client made so far.
/// </summary>
internal sealed class FakeJobs(Action<string?, string, object?, string?, string?> respond)
{
    private const int DefaultMaxBytes = 64 * 1024;
    private const int MaxBytes = 256 * 1024;

    private sealed class Job
    {
        public required string Id;
        public string Type = "bash";
        public string Status = "running";
        public string Label = "";
        public string? Command;
        public long StartTime;
        public long? EndTime;
        public int? ExitCode;
        public string? AgentId;
        public bool Waiting;
        /// <summary>A job that never streams (an async task): its text is answered as a snapshot.</summary>
        public bool Snapshot;
        /// <summary>Bytes no longer kept: <see cref="Output"/> holds the ones after them.</summary>
        public long Dropped;
        public byte[] Output = [];
        public long Total => Dropped + Output.Length;
    }

    private readonly object _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly JsonArray _controls = [];
    private bool _replaced;
    private string? _cancelError;
    private string? _outputError;
    /// <summary>get_jobs answers held back (<c>holdList</c>) until a <c>release</c>.</summary>
    private bool _holdList;
    private readonly List<string?> _heldLists = [];

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The jobs omp would list once the subagents scenario's turn left work running (its old /jobs listing).</summary>
    private static List<Job> SubagentScenarioJobs()
    {
        var now = Now;
        return
        [
            new Job { Id = "bg-1", Label = "npm run dev", Command = "npm run dev", StartTime = now - 120_000, Output = Encoding.UTF8.GetBytes("> dev\nready on http://localhost:3000\n") },
            new Job { Id = "bg-2", Type = "task", Label = "Watch the dev server", StartTime = now - 60_000, AgentId = "sa-3", Snapshot = true, Output = Encoding.UTF8.GetBytes("Watching the dev server") },
            new Job { Id = "bg-0", Status = "completed", Label = "npm test -- --watch=false", Command = "npm test -- --watch=false", StartTime = now - 240_000, EndTime = now - 200_000, ExitCode = 0, Output = Encoding.UTF8.GetBytes("12 passed\n") },
        ];
    }

    private static object Row(Job j) => new
    {
        id = j.Id, type = j.Type, status = j.Status, label = j.Label, command = j.Command, startTime = j.StartTime, endTime = j.EndTime,
        exitCode = j.ExitCode, agentId = j.AgentId, outputBytes = j.Snapshot ? 0 : j.Total, waitingForInput = j.Status == "running" && j.Waiting,
    };

    private static bool IsContinuation(byte b) => (b & 0xc0) == 0x80;

    /// <summary>Answers a jobs command; false when <paramref name="type"/> is not one. <paramref name="seeded"/>: the subagents scenario's jobs apply.</summary>
    public bool Handle(string? id, string type, JsonNode? cmd, bool seeded)
    {
        lock (_gate)
        {
            if (!_replaced && seeded && _jobs.Count == 0) _jobs.AddRange(SubagentScenarioJobs());
            switch (type)
            {
                case "get_jobs":
                    _controls.Add(new JsonObject { ["type"] = type });
                    if (_holdList) _heldLists.Add(id);
                    else RespondList(id);
                    return true;
                case "cancel_job":
                {
                    var jobId = cmd?["jobId"]?.GetValue<string>() ?? "";
                    _controls.Add(new JsonObject { ["type"] = type, ["jobId"] = jobId });
                    if (_cancelError is { } refused) { respond(id, type, null, refused, null); return true; }
                    var job = _jobs.FirstOrDefault(j => j.Id == jobId && j.Status == "running");
                    if (job is not null) { job.Status = "cancelled"; job.EndTime = Now; }
                    respond(id, type, new { cancelled = job is not null }, null, null);
                    return true;
                }
                case "get_job_output":
                {
                    var jobId = cmd?["jobId"]?.GetValue<string>() ?? "";
                    long? from = cmd?["fromByte"]?.GetValue<long>();
                    _controls.Add(new JsonObject { ["type"] = type, ["jobId"] = jobId, ["fromByte"] = from });
                    if (_outputError is { } failure) { respond(id, type, null, failure, null); return true; }
                    if (_jobs.FirstOrDefault(j => j.Id == jobId) is not { } job) { respond(id, type, null, "Unknown background job: " + jobId, null); return true; }
                    var max = cmd?["maxBytes"]?.GetValue<int>() ?? DefaultMaxBytes;
                    if (max is < 1 or > MaxBytes) { respond(id, type, null, $"maxBytes must be an integer from 1 to {MaxBytes}", null); return true; }
                    respond(id, type, Read(job, from, max), null, null);
                    return true;
                }
                case "test_job_control":
                    Control(cmd);
                    respond(id, type, new { controls = _controls.DeepClone(), held = _heldLists.Count }, null, null);
                    return true;
                default:
                    return false;
            }
        }
    }

    private static object Read(Job job, long? fromByte, int max)
    {
        var waiting = job.Status == "running" && job.Waiting;
        if (job.Snapshot)
            return new { jobId = job.Id, status = job.Status, waitingForInput = waiting, fromByte = 0, nextByte = 0, totalBytes = 0, startByte = 0, skippedBytes = 0, reset = false, snapshot = true, text = Encoding.UTF8.GetString(job.Output) };
        var reset = fromByte > job.Total;
        var tail = fromByte is null || reset;
        var wanted = tail ? job.Total - max : fromByte!.Value;
        var start = (int)Math.Clamp(wanted - job.Dropped, 0, job.Output.Length);
        while (start < job.Output.Length && IsContinuation(job.Output[start])) start++;
        var end = Math.Min(job.Output.Length, start + max);
        if (end < job.Output.Length) while (end > start && IsContinuation(job.Output[end])) end--;
        // Like the engine: a page always holds one whole character, so the cursor moves
        if (end == start && start < job.Output.Length) for (end = start + 1; end < job.Output.Length && IsContinuation(job.Output[end]); end++) { }
        return new
        {
            jobId = job.Id, status = job.Status, waitingForInput = waiting, fromByte = job.Dropped + start, nextByte = job.Dropped + end,
            totalBytes = job.Total, startByte = job.Dropped, skippedBytes = tail ? 0 : Math.Max(0, job.Dropped + start - fromByte!.Value),
            reset, snapshot = false, text = Encoding.UTF8.GetString(job.Output, start, end - start),
        };
    }

    private void RespondList(string? id) =>
        respond(id, "get_jobs", new { jobs = _jobs.Where(j => j.Status == "running").Concat(_jobs.Where(j => j.Status != "running").OrderByDescending(j => j.StartTime)).Select(Row).ToArray() }, null, null);

    private void Control(JsonNode? cmd)
    {
        if (cmd?["jobs"] is JsonArray jobs)
        {
            _replaced = true;
            _jobs.Clear();
            var now = Now;
            foreach (var j in jobs.OfType<JsonObject>())
                _jobs.Add(new Job
                {
                    Id = j["id"]!.GetValue<string>(),
                    Type = j["type"]?.GetValue<string>() ?? "bash",
                    Status = j["status"]?.GetValue<string>() ?? "running",
                    Label = j["label"]?.GetValue<string>() ?? "",
                    Command = j["command"]?.GetValue<string>(),
                    StartTime = now - (j["ageMs"]?.GetValue<long>() ?? 1000),
                    EndTime = j["status"]?.GetValue<string>() is null or "running" ? null : now,
                    ExitCode = j["exitCode"]?.GetValue<int>(),
                    Waiting = j["waitingForInput"]?.GetValue<bool>() == true,
                    Snapshot = j["snapshot"]?.GetValue<bool>() == true,
                    Output = Encoding.UTF8.GetBytes(j["output"]?.GetValue<string>() ?? ""),
                });
        }
        var target = cmd?["jobId"]?.GetValue<string>() is { } jobId ? _jobs.FirstOrDefault(j => j.Id == jobId) : null;
        if (target is not null)
        {
            if (cmd?["append"]?.GetValue<string>() is { } text) target.Output = [.. target.Output, .. Encoding.UTF8.GetBytes(text)];
            if (cmd?["drop"]?.GetValue<int>() is { } drop)
            {
                drop = Math.Min(drop, target.Output.Length);
                target.Dropped += drop;
                target.Output = target.Output[drop..];
            }
            if (cmd?["waitingForInput"]?.GetValue<bool>() is { } waiting) target.Waiting = waiting;
            if (cmd?["status"]?.GetValue<string>() is { } status)
            {
                target.Status = status;
                target.EndTime = status == "running" ? null : Now;
                target.ExitCode = cmd["exitCode"]?.GetValue<int>();
            }
        }
        if (cmd?.AsObject().ContainsKey("cancelError") == true) _cancelError = cmd["cancelError"]?.GetValue<string>();
        if (cmd?.AsObject().ContainsKey("outputError") == true) _outputError = cmd["outputError"]?.GetValue<string>();
        if (cmd?["holdList"]?.GetValue<bool>() is { } hold) _holdList = hold;
        if (cmd?["release"]?.GetValue<bool>() == true)
        {
            foreach (var held in _heldLists) RespondList(held);
            _heldLists.Clear();
        }
    }
}

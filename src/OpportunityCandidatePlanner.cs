using System;
using System.Collections.Generic;
using System.Linq;

public enum OpportunityCandidateSource
{
    LiveDelay,
    Historical,
    Exploration,
    Fill
}

public sealed class OpportunityCandidateChoice
{
    public OpportunityCandidateChoice(string name, OpportunityCandidateSource source)
    {
        Name = name;
        Source = source;
    }

    public string Name { get; private set; }
    public OpportunityCandidateSource Source { get; private set; }
}

public sealed class OpportunityCandidatePlan
{
    public OpportunityCandidatePlan(IList<OpportunityCandidateChoice> candidates,
        int liveDelayCount, int historicalCount, int explorationCount,
        int fillCount, int duplicateRemovalCount)
    {
        Candidates = candidates ?? new List<OpportunityCandidateChoice>();
        LiveDelayCount = liveDelayCount;
        HistoricalCount = historicalCount;
        ExplorationCount = explorationCount;
        FillCount = fillCount;
        DuplicateRemovalCount = duplicateRemovalCount;
    }

    public IList<OpportunityCandidateChoice> Candidates { get; private set; }
    public int LiveDelayCount { get; private set; }
    public int HistoricalCount { get; private set; }
    public int ExplorationCount { get; private set; }
    public int FillCount { get; private set; }
    public int DuplicateRemovalCount { get; private set; }
}

public static class OpportunityCandidatePlanner
{
    public const int MaximumCandidates = 8;
    public const int LiveDelayQuota = 5;
    public const int HistoricalQuota = 2;

    public static OpportunityCandidatePlan Create(IEnumerable<CandidateNode> candidates,
        IDictionary<string, int> delays, string current, ExperienceData experience,
        string scope, DateTime now)
    {
        string[] names = (candidates ?? Enumerable.Empty<CandidateNode>())
            .Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name) && x.Name != current)
            .Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
        string[] liveRanked = names.OrderBy(x => ValidDelay(delays, x))
            .ThenBy(x => x, StringComparer.Ordinal).ToArray();
        string[] live = liveRanked.Take(LiveDelayQuota).ToArray();
        var reserved = new HashSet<string>(live, StringComparer.Ordinal);

        NodeExperience[] recommendations = experience == null || experience.Nodes == null
            ? new NodeExperience[0]
            : experience.Recommend(scope, names, now).ToArray();
        int duplicateRemovalCount = recommendations.Count(x => reserved.Contains(x.Node));
        string[] historical = recommendations.Select(x => x.Node)
            .Where(x => !reserved.Contains(x)).Distinct(StringComparer.Ordinal)
            .Take(HistoricalQuota).ToArray();
        foreach (string name in historical) reserved.Add(name);

        string exploration = names.Where(x => !reserved.Contains(x))
            .Select(x => new { Name = x, Experience = FindExperience(experience, scope, x) })
            .OrderBy(x => x.Experience != null)
            .ThenBy(x => x.Experience == null ? 0 : x.Experience.Samples)
            .ThenBy(x => x.Experience == null ? DateTime.MinValue : x.Experience.LastUtc)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).FirstOrDefault();
        if (exploration != null) reserved.Add(exploration);

        var ordered = new List<OpportunityCandidateChoice>();
        AddAt(ordered, live, 0, OpportunityCandidateSource.LiveDelay);
        AddAt(ordered, historical, 0, OpportunityCandidateSource.Historical);
        if (exploration != null)
            ordered.Add(new OpportunityCandidateChoice(exploration, OpportunityCandidateSource.Exploration));
        AddAt(ordered, live, 1, OpportunityCandidateSource.LiveDelay);
        AddAt(ordered, historical, 1, OpportunityCandidateSource.Historical);
        for (int index = 2; index < live.Length; index++)
            AddAt(ordered, live, index, OpportunityCandidateSource.LiveDelay);

        int fillCount = 0;
        foreach (string name in liveRanked.Where(x => !reserved.Contains(x)))
        {
            if (ordered.Count >= MaximumCandidates) break;
            ordered.Add(new OpportunityCandidateChoice(name, OpportunityCandidateSource.Fill));
            reserved.Add(name);
            fillCount++;
        }
        if (ordered.Count > MaximumCandidates)
            ordered.RemoveRange(MaximumCandidates, ordered.Count - MaximumCandidates);
        return new OpportunityCandidatePlan(ordered, live.Length, historical.Length,
            exploration == null ? 0 : 1, fillCount, duplicateRemovalCount);
    }

    private static int ValidDelay(IDictionary<string, int> delays, string name)
    {
        int value;
        return delays != null && delays.TryGetValue(name, out value) && value > 0
            ? value : Int32.MaxValue;
    }

    private static NodeExperience FindExperience(ExperienceData experience, string scope, string name)
    {
        return experience == null || experience.Nodes == null ? null :
            experience.Nodes.FirstOrDefault(x => x != null && x.Scope == scope && x.Node == name);
    }

    private static void AddAt(List<OpportunityCandidateChoice> output, string[] values,
        int index, OpportunityCandidateSource source)
    {
        if (index >= 0 && index < values.Length)
            output.Add(new OpportunityCandidateChoice(values[index], source));
    }
}

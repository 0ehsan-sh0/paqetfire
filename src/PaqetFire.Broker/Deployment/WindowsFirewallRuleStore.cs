namespace PaqetFire.Broker.Deployment;

public sealed class WindowsFirewallRuleStore : IFirewallRuleStore
{
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int AllProfiles = 0x7FFFFFFF;
    private const int MaximumDuplicateRemovals = 16;

    public IReadOnlyList<FirewallRule> FindByName(string name) =>
        FindByNames([name]).GetValueOrDefault(name) ?? [];

    public IReadOnlyDictionary<string, IReadOnlyList<FirewallRule>> FindByNames(IReadOnlyCollection<string> names)
    {
        // INetFwRules.Item returns only the first rule with a name, so duplicates need
        // enumeration. A machine can hold hundreds of rules behind slow COM calls, so the
        // collection is walked once for every requested name together.
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, List<FirewallRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic rule in CreatePolicy().Rules)
        {
            var name = (string?)rule.Name;
            if (name is null || !wanted.Contains(name))
            {
                continue;
            }

            if (!matches.TryGetValue(name, out var list))
            {
                matches[name] = list = [];
            }

            list.Add(new FirewallRule(
                name,
                (string?)rule.Description ?? string.Empty,
                (string?)rule.ApplicationName ?? string.Empty,
                (FirewallProtocol)(int)rule.Protocol,
                (string?)rule.RemoteAddresses ?? string.Empty,
                (bool)rule.Enabled,
                (int)rule.Direction == InboundDirection &&
                (int)rule.Action == AllowAction &&
                ((int)rule.Profiles & AllProfiles) == AllProfiles));
        }

        return matches.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<FirewallRule>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public void Replace(FirewallRule rule)
    {
        var rules = CreatePolicy().Rules;
        // Remove removes one rule with the name per call; one count avoids re-enumerating
        // the whole collection after every removal.
        var existing = Math.Min(FindByName(rule.Name).Count, MaximumDuplicateRemovals);
        for (var removal = 0; removal < existing; removal++)
        {
            rules.Remove(rule.Name);
        }

        dynamic created = Activator.CreateInstance(
            Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        created.Name = rule.Name;
        created.Description = rule.Description;
        created.ApplicationName = rule.ProgramPath;
        created.Protocol = (int)rule.Protocol;
        created.RemoteAddresses = rule.RemoteAddresses;
        created.Direction = InboundDirection;
        created.Action = AllowAction;
        created.Profiles = AllProfiles;
        created.Enabled = true;
        rules.Add(created);
    }

    private static dynamic CreatePolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
}

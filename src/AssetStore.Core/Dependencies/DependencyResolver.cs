// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Dependencies;

/// <summary>Resolves the transitive dependency closure of store assets (by id).</summary>
public static class DependencyResolver
{
    /// <summary>
    /// Computes the transitive set of dependency ids for <paramref name="rootId"/>, excluding the root.
    /// </summary>
    /// <param name="rootId">The asset whose dependencies are resolved.</param>
    /// <param name="directDependencies">Maps an id to its directly declared dependency ids.</param>
    /// <returns>A resolution result: ordered ids, missing ids, and any detected cycle.</returns>
    public static ResolutionResult Resolve(
        string rootId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> directDependencies)
    {
        var resolved = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string>? cycle = null;

        void Visit(string id, List<string> path)
        {
            if (!onStack.Add(id))
            {
                var start = path.IndexOf(id);
                cycle ??= path.Skip(start < 0 ? 0 : start).Append(id).ToList();
                return;
            }

            path.Add(id);

            if (directDependencies.TryGetValue(id, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (!directDependencies.ContainsKey(dep))
                    {
                        missing.Add(dep);
                    }

                    if (visited.Add(dep))
                    {
                        Visit(dep, path);
                        if (!missing.Contains(dep))
                        {
                            resolved.Add(dep);
                        }
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            onStack.Remove(id);
        }

        Visit(rootId, []);

        return new ResolutionResult(
            resolved.Where(directDependencies.ContainsKey).Distinct(StringComparer.Ordinal).ToList(),
            missing.ToList(),
            cycle);
    }
}

/// <summary>Outcome of a dependency resolution.</summary>
/// <param name="Dependencies">Transitive dependency ids known to the registry (excludes the root).</param>
/// <param name="Missing">Referenced ids that are not present in the registry.</param>
/// <param name="Cycle">A detected dependency cycle, or null if none.</param>
public sealed record ResolutionResult(
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string>? Cycle)
{
    public bool HasCycle => Cycle is { Count: > 0 };

    public bool HasMissing => Missing.Count > 0;
}

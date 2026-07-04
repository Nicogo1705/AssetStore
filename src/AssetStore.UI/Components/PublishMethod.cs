// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Components;

/// <summary>How a registry pull request gets opened from the manage page.</summary>
public enum PublishMethod
{
    /// <summary>The store opens the PR with a pasted GitHub token.</summary>
    Assisted,

    /// <summary>The user copies the payload and opens the PR on GitHub themselves.</summary>
    Manual,

    /// <summary>The desktop app opens the PR through the local GitHub CLI (gh).</summary>
    Cli,
}

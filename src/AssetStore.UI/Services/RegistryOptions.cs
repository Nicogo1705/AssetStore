// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Services;

/// <summary>
/// Where the registry lives. Bound from configuration (section "Registry") so the whole app can be
/// pointed at a different owner/repo — e.g. a Stride community org, if ever adopted — without code changes.
/// </summary>
public sealed class RegistryOptions
{
    public string Owner { get; init; } = "Nicogo1705";

    public string Repo { get; init; } = "AssetContainer";

    public string BaseBranch { get; init; } = "main";
}

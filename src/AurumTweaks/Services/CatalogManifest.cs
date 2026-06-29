using System;
using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>
/// The trust anchor for the tweak catalog: a SHA-256 (lowercase hex) of every JSON file shipped under
/// <c>/Tweaks</c>, keyed by its catalog-relative path (forward slashes). Compiled into the assembly so it
/// lives in the admin-owned binary, NOT in the (possibly user-writable) data directory next to it — that is
/// the whole point. <see cref="TweakRepository"/> checks each file against this list before loading it and
/// refuses anything unknown or edited (see <see cref="CatalogIntegrity"/> for the threat model).
///
/// MAINTENANCE: this is the deliberate, human-reviewed allow-list of catalog content. When a shipped JSON
/// legitimately changes, this table must be updated in the same commit — and that is enforced, not trusted:
/// <c>CatalogManifestSyncTests</c> recomputes the hashes from the real catalog and fails loudly (printing the
/// exact entries to paste) if anything here is stale, missing, or extra. Updating the trusted set is meant to
/// be an explicit act, so the elevated executor's input never changes silently.
/// </summary>
internal static class CatalogManifest
{
    public static readonly IReadOnlyDictionary<string, string> Hashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["advanced/01-performance.json"] = "7e47cf597f5f883fa49f08383123033337bab638dc10e1831efd5cb42194021e",
            ["advanced/02-services-debloat.json"] = "f2f68f71d07bc7458d1bd59277c7ce7e310e4851557753cbbb45b097e3b90889",
            ["advanced/03-network.json"] = "09ba4f99760d57cf8d8f34c2244bf3413f7614cdfcfb61c1cfbb19e7cb32bf03",
            ["advanced/04-power-perf.json"] = "7a2816276a1c1b1c4af0b0bcc0302139395073587565f3a8876258c4630e64d3",
            ["advanced/05-debloat-extra.json"] = "dd1712e497f00f909b83a6adbd20c10591bf2beea09435cdd0ac05070f73f727",
            ["advanced/06-cpu-scheduler.json"] = "d37fbb0236e7783b55ecc874323a86de0204c23169923867beb34f6500c50cda",
            ["advanced/07-input-latency.json"] = "fcf10244e59197e7082bf7d19f8300a3aa2837cb083a84a88c3371128b3155ad",
            ["advanced/08-gpu-vendor.json"] = "0bb96743d3399190c0e8f89aafdbcb51d23e62c0ee33b5ba2e4cba7910483564",
            ["advanced/09-network-latency.json"] = "07013d0947b2ea1408a3ab1e8c4c1c5b472a82371f73106cdbc23437930cd272",
            ["advanced/10-latency-power.json"] = "8f4c66bd258a2f380f995a9136729217366a1439112d4196b3ff673008849179",
            ["advanced/11-gpu-render.json"] = "927d7f9ce9b1f62782383d00561ac02a4f493f955839b36a3ccb7c4e7088f8a7",
            ["advanced/12-memory-storage.json"] = "7d23ce51c4af8f238ef0d7e25fd824fbf5f53f16e3514e6aad77b7dd0b9177a9",
            ["extreme/01-extreme.json"] = "93affee1045b17f1b261cabc494e759c86f32e20eb3de42563ce60417a442f0f",
            ["extreme/02-extreme-network.json"] = "ce04d8105cc1a54d353a08370225bc98ff3c4b543fd69e6be456b41f5fb49058",
            ["extreme/03-cpu-power.json"] = "f79dbe776e8b36847de3c66fde7f8b80125edd92421d98ab5ae0c16ce1cc2dfc",
            ["tranquille/01-gaming-baseline.json"] = "258d9fc508768b61545482e02be923216a6fab6b6bf17e9bbe0fba2cb205d164",
            ["tranquille/02-privacy.json"] = "7f174d79a20c74b87ccbe59edb77bdc8d92002346e1e1e2c5098c96a715c0b6f",
            ["tranquille/03-ui-qol.json"] = "da2a2843edfe383a0440c6f649fd1b1a407fc0317f024f14bf3809cf41c67b23",
            ["tranquille/04-explorer-ui.json"] = "97c318c37ec69c0e884227de9c156a99e11a02c76dbbac07389702c0fcaabaee",
            ["tranquille/05-win11-ai.json"] = "12e5f6f9641c73aa4da5d774abe7c461ef6281b3ada9a8f8ef6d3ceafc5db822",
            ["tranquille/06-qol-startup.json"] = "d18d0f8997111351b794ad3680ddf2fa5d74d4c77879841e6c9a205bb8f70002",
            ["tranquille/07-input-qol.json"] = "33ab013dd1f2e85fdc7be6826cb1245493095b5d3fe081ec56198fed1f12f98e",
        };
}

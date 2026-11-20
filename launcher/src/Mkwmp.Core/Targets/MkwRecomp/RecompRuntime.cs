// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The mkw-recomp build this launcher installs. One entry per launcher version, changed by hand
/// in a launcher release (the <c>bump-mkw-recomp</c> workflow): the values come from the release
/// itself (<c>gh api repos/anarqz/mkw-recomp/releases/tags/&lt;tag&gt;</c>: the asset's <c>size</c> and
/// <c>digest</c>) and from the zip (the SHA-256 of its mkw.exe, which must be the one in
/// <see cref="Recomp.Known"/>; the sum of its entries' sizes).
/// <para>
/// mkw-recomp is Adrià's work (MIT, github.com/adriadam10/mkw-recomp). MKWMP installs its own
/// fork of it, which adds what the MKWMP mod needs to the plugin API and nothing else. The zip
/// holds the runtime and the tools its setup uses; it holds nothing of the game.
/// </para>
/// </summary>
public static class RecompRuntime
{
    /// <summary>The MIT notice of mkw-recomp, as its release ships it in <c>licenses/mkw-recomp-LICENSE</c>.</summary>
    public const string Notice =
        """
        MIT License

        Copyright (c) 2026 Adrià

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    /// <summary>What the window says wherever mkw-recomp is installed or named (About, the install step).</summary>
    public const string CreditSummary =
        "The game runs on mkw-recomp, a static recompilation of Mario Kart World by Adrià and its contributors (MIT License). "
        + "MKWMP installs its own build of it, with the additions online play needs. mkw-recomp builds the game from your own "
        + "copy, on your device; MKWMP is not affiliated with mkw-recomp or with Nintendo.";

    /// <summary>
    /// The pinned build: mkw-recomp 0.1.16-mkwmp, the Windows zip (also what runs under Proton).
    /// The release's no-AVX2 zip is not installed by the launcher; its mkw.exe is in <see cref="Recomp.Known"/>.
    /// </summary>
    public static RuntimeManifest Pinned { get; } = new()
    {
        Name = "mkw-recomp",
        Version = "0.1.16-mkwmp",
        Repository = "anarqz/mkw-recomp",
        Tag = "v0.1.16-mkwmp",
        Asset = "mkw-recomp-0.1.16-mkwmp-windows-x64.zip",
        Size = 221_144_444,
        Sha256 = "cf41cb0cdab4af58a2aff586e9be3d09ef00f4a85699319675fb443928fc365d",
        Executable = Recomp.Exe,
        ExecutableSha256 = "73997d10f8cca878e9654b174c194833053a529ec04ff3b03be587c1e592eb8f",
        // 23,344 files under one "mkw-recomp/" folder; 698,604,837 bytes of them are setup/ (clang, zig, glslc, python).
        InstalledSize = 738_675_063,
        // Measured on an install of game 1.8.0 set up with "link" (the game's data is not copied):
        // spirv 1.80 GB, pipeline cache 0.49 GB, setup-work 0.37 GB, mkwgame.dll 0.23 GB, exefs,
        // sm86 and python's caches 0.07 GB: 2.96 GB, rounded up.
        SetupSpace = 3_500_000_000,
        LineageSuffix = "-mkwmp",
        FolderName = "mkw-recomp",
        Preserved =
        [
            // Not "setup": the bundled toolchain and the setup's sources are the runtime's, and setup\ changed from
            // 0.1.15-mkwmp to 0.1.16-mkwmp (setup\src is hashed into the setup's step keys and the game id,
            // launcher/docs/RECOMP_INTEGRATION.md, 1.5). An update compares its files one by one and replaces only
            // those that changed, with the backup and rollback of any other file.
            // the setup's state and work
            "setup-state.json", "setup.lock", "setup-work",
            // the player's settings, saves, mods and logs
            "mkw.ini", "profile", "mods", "logs",
            // made on this machine from the player's game: never replaced, never downloaded
            "mkwgame.dll", "mkwgame.dll.pdb", "mkwgame.id", "exefs", "romfs", "sm86", "spirv", "spirv-*", "pipeline-*.bin", "seq_nodes-*.tsv",
            // MKWMP's own session file
            "mkwmp.toml",
        ],
        Credit = new RuntimeCredit(
            CreditSummary,
            "mkw-recomp",
            new Uri("https://github.com/adriadam10/mkw-recomp"),
            new Uri("https://github.com/anarqz/mkw-recomp/tree/v0.1.16-mkwmp"),
            "MIT",
            Notice,
            "licenses") { SummaryKey = "core.recomp.credit" },
    };
}

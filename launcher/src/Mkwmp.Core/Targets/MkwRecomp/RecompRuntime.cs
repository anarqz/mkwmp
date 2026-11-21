// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.Intrinsics.X86;

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
    /// The pinned build for this machine's processor: <see cref="Main"/> when it has what x86-64-v3 asks
    /// (AVX2, FMA and BMI2: the checks <c>require_x86_64_v3</c> in mkw.exe makes; .NET answers them under
    /// Wine / Proton too), else <see cref="NoAvx2"/>.
    /// </summary>
    public static RuntimeManifest Pinned => For(Avx2.IsSupported && Fma.IsSupported && Bmi2.IsSupported);

    /// <summary>The pinned build for a processor with (<paramref name="v3"/>) or without AVX2: the tests' switch.</summary>
    public static RuntimeManifest For(bool v3) => v3 ? Main : NoAvx2;

    /// <summary>
    /// The pinned build: mkw-recomp 0.1.16-mkwmp.1, the Windows zip (also what runs under Proton: .1 is the
    /// setup under Wine, where zig cannot use its cache, and the game window under Proton on a Linux desktop).
    /// Same game id as 0.1.16-mkwmp, so an installed game keeps its compiled code and shaders.
    /// </summary>
    public static RuntimeManifest Main { get; } = new()
    {
        Name = "mkw-recomp",
        Version = "0.1.16-mkwmp.1",
        Repository = "anarqz/mkw-recomp",
        Tag = "v0.1.16-mkwmp.1",
        Asset = "mkw-recomp-0.1.16-mkwmp.1-windows-x64.zip",
        Size = 221_145_220,
        Sha256 = "313bd96d9f8fdbd5844debc35dbd759c2f3bd54ed87496b591e8c737531e970e",
        Executable = Recomp.Exe,
        ExecutableSha256 = "d04dc49a14618dce8385cdb1c3e61ba1bd62a2e5938ddf7dd216c926517cc72d",
        // 23,344 files under one "mkw-recomp/" folder; most of the size is setup/ (clang, zig, glslc, python).
        InstalledSize = 738_676_052,
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
            new Uri("https://github.com/anarqz/mkw-recomp/tree/v0.1.16-mkwmp.1"),
            "MIT",
            Notice,
            "licenses") { SummaryKey = "core.recomp.credit" },
    };

    /// <summary>
    /// The same release's zip for processors without AVX2 (an AMD FX, an Intel Core before Haswell): mkw.exe
    /// built for x86-64-v2 (SSE4.2), and <c>setup/cpu-level</c> ("v2"), so its setup builds the game code for
    /// v2 too. Everything else is the main zip's. Its version is the same: on a processor with AVX2 an install
    /// of it is up to date (it runs there); on one without, an install of <see cref="Main"/> is not.
    /// </summary>
    public static RuntimeManifest NoAvx2 { get; } = Main with
    {
        Asset = "mkw-recomp-0.1.16-mkwmp.1-windows-x64-noavx2.zip",
        Size = 221_040_229,
        Sha256 = "c1d163f4a787c0bfcae6f28722e5684f13fe7e818bbc8620bb45f88dedd0b3ac",
        ExecutableSha256 = "59af1fcc9c3a233e2d86c8437224f90a958e9ecb473118d71d92090379fbe3c0",
        // 23,345 files: the main zip's, its own mkw.exe, and setup/cpu-level.
        InstalledSize = 738_395_479,
        Variant = "no AVX2",
        VariantKey = "core.runtime.variant_noavx2",
        // The main zip's mkw.exe needs AVX2: it would not start here.
        Unsuitable = [Main.ExecutableSha256],
    };
}

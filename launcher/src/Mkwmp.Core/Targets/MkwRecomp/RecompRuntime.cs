// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.Intrinsics.X86;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The mkw-recomp build this launcher installs. One entry per launcher version, changed by hand
/// in a launcher release (the <c>bump-mkw-recomp</c> workflow): the values come from the release
/// itself (<c>gh api repos/adriadam10/mkw-recomp/releases/tags/&lt;tag&gt;</c>: the asset's <c>size</c> and
/// <c>digest</c>) and from the zip (the SHA-256 of its mkw.exe, which must be the one in
/// <see cref="Recomp.Known"/>; the sum of its entries' sizes).
/// <para>
/// mkw-recomp is Adrià's work (MIT, github.com/adriadam10/mkw-recomp). From 0.1.17 MKWMP installs its
/// official release: everything the MKWMP mod needs (plugin API 4, the setup and the window under
/// Wine / Proton) is upstream there. Up to 0.1.16-mkwmp.1 it installed its own fork (anarqz/mkw-recomp).
/// The zip holds the runtime and the tools its setup uses; it holds nothing of the game.
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
        + "MKWMP installs its official release. mkw-recomp builds the game from your own "
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
    /// The pinned build: the official mkw-recomp 0.1.18, the Windows zip (also what runs under Proton). It has
    /// what the fork's 0.1.16-mkwmp.1 added (plugin API 4, upstream #743; the setup under Wine, #738; no SDL x11
    /// driver under Proton, #739) and the same game id as 0.1.16 and 0.1.17 (c9bbcb75a9ac9a49), so an installed game
    /// keeps its compiled code. 0.1.18 fixes the silent 0xC0000409 exit on Windows 11 at a guest thread's end (#751)
    /// and downloads DLSS, FSR 3.1 and RenHDrum when they are picked (<see cref="RecompLibs"/>). Our fork's two
    /// unreleased Wine fixes are done without it: shadow storage by the launcher (<see cref="RecompLaunchEnv"/>), the
    /// early XInput ask by the mod (native/probe/src/winepad.c).
    /// </summary>
    public static RuntimeManifest Main { get; } = new()
    {
        Name = "mkw-recomp",
        Version = "0.1.18",
        Repository = "adriadam10/mkw-recomp",
        Tag = "v0.1.18",
        Asset = "mkw-recomp-0.1.18-windows-x64.zip",
        Size = 221_949_886,
        Sha256 = "ff655d6dc566d315c9f33f96b9b94b7dc7e9c1b383091ed36c7f35921b6444c8",
        Executable = Recomp.Exe,
        ExecutableSha256 = "84874fe56ea156ee7e10095d9f1c70a3877a5c8f280feb43ecc5c9a94678ce06",
        // 23,349 files under one "mkw-recomp/" folder; most of the size is setup/ (clang, zig, glslc, python).
        InstalledSize = 741_365_270,
        // Measured on an install of game 1.8.0 set up with "link" (the game's data is not copied):
        // spirv 1.80 GB, pipeline cache 0.49 GB, setup-work 0.37 GB, mkwgame.dll 0.23 GB, exefs,
        // sm86 and python's caches 0.07 GB: 2.96 GB, rounded up.
        SetupSpace = 3_500_000_000,
        // The fork's builds this launcher installed before (0.1.15-mkwmp ... 0.1.16-mkwmp.1): an install of one is
        // updated in place to the official release, keeping the game, its setup and the player's saves (Preserved).
        LineageSuffix = "-mkwmp",
        // An official release installed by hand that the MKWMP mod ran on (0.1.15 to 0.1.17) is updated in place too:
        // players who picked their own install were left on 0.1.17 (fixed in MKWMP 1.4.7). All three have 0.1.18's
        // game id (c9bbcb75a9ac9a49): the game code stays. Older releases have no plugin API and are left alone.
        UpdatesFrom = ["0.1.15", "0.1.16", "0.1.17"],
        FolderName = "mkw-recomp",
        Preserved =
        [
            // Not "setup": the bundled toolchain and the setup's sources are the runtime's, and setup\ changed from
            // 0.1.15-mkwmp to 0.1.16-mkwmp (setup\src is hashed into the setup's step keys and the game id,
            // launcher/docs/RECOMP_INTEGRATION.md, 1.5). An update compares its files one by one and replaces only
            // those that changed, with the backup and rollback of any other file. Files the zip does not hold are never
            // touched: the DLSS, FSR 3.1 and RenHDrum libraries next to mkw.exe (nvngx_dlss*.dll,
            // amd_fidelityfx_*_vk.dll, renhdrum.dll; no release ships them: 0.1.18's libs.py and RecompLibs download
            // them, or the player puts them there) stay.
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
            new Uri("https://github.com/adriadam10/mkw-recomp/tree/v0.1.18"),
            "MIT",
            Notice,
            "licenses")
        {
            SummaryKey = "core.recomp.credit",
            // "Copyright (c) 2026 Adrià" (its LICENSE); github.com/adriadam10 owns the project.
            Author = "Adrià",
            AuthorUrl = new Uri("https://github.com/adriadam10"),
            LicenseUrl = new Uri("https://github.com/adriadam10/mkw-recomp/blob/main/LICENSE"),
        },
    };

    /// <summary>
    /// The same release's zip for processors without AVX2 (an AMD FX, an Intel Core before Haswell): mkw.exe
    /// built for x86-64-v2 (SSE4.2), and <c>setup/cpu-level</c> ("v2"), so its setup builds the game code for
    /// v2 too. Everything else is the main zip's. Its version is the same: on a processor with AVX2 an install
    /// of it is up to date (it runs there); on one without, an install of <see cref="Main"/> is not.
    /// </summary>
    public static RuntimeManifest NoAvx2 { get; } = Main with
    {
        Asset = "mkw-recomp-0.1.18-windows-x64-noavx2.zip",
        Size = 221_839_821,
        Sha256 = "7ce3148d23a2fe255ee36536bd24e027bc4313c54eff50a590e73cb33fa32ccc",
        ExecutableSha256 = "38b545ce7f017183c8919f958311e3d6b6083c61326f27b95d4a6fcff4513f83",
        // 23,350 files: the main zip's, its own mkw.exe, and setup/cpu-level.
        InstalledSize = 741_077_529,
        Variant = "no AVX2",
        VariantKey = "core.runtime.variant_noavx2",
        // The main zip's mkw.exe needs AVX2: it would not start here.
        Unsuitable = [Main.ExecutableSha256],
    };
}

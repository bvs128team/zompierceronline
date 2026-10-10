# Licenses

ZompiercerLAN itself (`BepInEx/plugins/ZompiercerLAN.dll`, `BepInEx/plugins/ZompiercerLAN.Network/ZompiercerLAN.Network.exe` and its `.config`, the setup and uninstall scripts) is licensed under the MIT License: `ZompiercerLAN/LICENSE.txt`. The components below keep their own licenses.

Downloaded 2026-10-03 from the official repositories at the tags matching the versions in this archive (file versions read from the DLLs). Bouncy Castle 2.7.0 is described in `BouncyCastle-2.7.0/PROVENANCE.md`.

| Component | License | File | Source | SHA-256 |
|---|---|---|---|---|
| BepInEx 5.4.23.5 (BepInEx.dll, BepInEx.Preloader.dll, BepInEx.Harmony.dll, HarmonyXInterop.dll, 0Harmony20.dll, doorstop_config.ini) | MIT | `BepInEx-5.4.23.5/LICENSE` | https://raw.githubusercontent.com/BepInEx/BepInEx/v5.4.23.5/LICENSE | `E6E534EF6F4347B6449407EE046A3D09CB0174C6F688C996AD0BED94B74B3933` |
| HarmonyX 2.9.0 (0Harmony.dll) | MIT | `HarmonyX-2.9.0/LICENSE` | https://raw.githubusercontent.com/BepInEx/HarmonyX/v2.9.0/LICENSE | `7E54AB4766EF78637A05FB3454663A66DE6BB960DBD4C7088890ED76B0844F36` |
| MonoMod 22.01.29.01 (MonoMod.RuntimeDetour.dll, MonoMod.Utils.dll) | MIT | `MonoMod-22.01.29.01/LICENSE` | https://raw.githubusercontent.com/MonoMod/MonoMod/v22.01.29.01/LICENSE | `0B64833222E3C6A426AD29362D26334619FE02EAF2B89EA9C37805521896C920` |
| Mono.Cecil 0.10.4 (Mono.Cecil*.dll) | MIT | `Mono.Cecil-0.10.4/LICENSE.txt` | https://raw.githubusercontent.com/jbevain/cecil/0.10.4/LICENSE.txt | `13D58ECBED676511C0C4C4009BFF107025E50007FF5E7901DC562700EC62509E` |
| UnityDoorstop 4.5.0 (winhttp.dll, bundled unmodified by BepInEx 5.4.23.5) | LGPL-2.1 | `UnityDoorstop-4.5.0/LICENSE` | https://raw.githubusercontent.com/NeighTools/UnityDoorstop/v4.5.0/LICENSE | `20C17D8B8C48A600800DFD14F95D5CB9FF47066A9641DDEAB48DC54AEC96E331` |

UnityDoorstop is used unmodified, exactly as shipped in the official BepInEx_win_x64_5.4.23.5.zip (SHA-256 82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4). Its source code: https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0 . You may replace winhttp.dll with your own build of that source.

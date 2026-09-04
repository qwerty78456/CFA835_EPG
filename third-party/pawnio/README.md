# PawnIO 2.2.0 (redistributed)

This directory redistributes the PawnIO kernel driver installer so a monitored machine — including
an air-gapped one with no internet access — can gain CPU temperature access without a separate
download. Nothing here is authored by this project.

## Why this is needed

`LibreHardwareMonitorLib` reads CPU temperatures through ring-0 registers (MSR on Intel, SMN/MSR on
AMD). It reaches them by opening the kernel device `\\?\GLOBALROOT\Device\PawnIO`, which only exists
while the PawnIO driver service is loaded. Without it, `--diagnose` reports `PawnIO: NOT INSTALLED`,
the `cpu.temperature` layout field renders its `fallback` text, and the thermal-warning LED never
arms. This is true on both Intel and AMD; it is not a CPU-vendor difference.

The PawnIO *modules* are already embedded in `LibreHardwareMonitorLib.dll` as resources
(`IntelMSR.bin`, `AMDFamily17.bin`, `LpcIO.bin`, and others), so only the driver itself is missing.

**Bundling does not remove the installation step.** A kernel-mode driver cannot run from an
application folder. It must be registered as a system service by an administrator, and on x64
Windows with driver signature enforcement it must carry a Microsoft-attested signature. What
bundling removes is the download and the version guesswork; `scripts/Install-Service.ps1` runs the
installer below when the driver is absent.

The application itself requires an elevated Administrator token for every mode. That guarantees the
process-side permission check, but it does not install or start PawnIO; the driver setup above remains
necessary for CPU temperature readings.

## Contents and provenance

| File | SHA-256 | Source |
|---|---|---|
| `PawnIO_setup.exe` | `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032` | <https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0> |
| `PawnIO-2.2.0-source.zip` | `93aa5d410b76c71e9004cac406ed19d0550a735410a9abe4d0c9a838b8b98eac` | <https://github.com/namazso/PawnIO> tag `2.2.0`, commit `5cdf470831fdfff3f7f1d06363ca6b230f3bf35a` |
| `COPYING` | `8177f97513213526df2cf6184d8ff986c675afb514d4e68a404010521b880643` | GNU General Public License version 2, from the same source tree |

`PawnIO_setup.exe` is 3,410,960 bytes and is redistributed **byte-for-byte unmodified**. Its
Authenticode signature was verified before it was committed:

```
Status         : Valid
SignerSubject  : E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU
SignerIssuer   : CN=GLOBALTRUST 2015 CODESIGNING 1, O=e-commerce monitoring GmbH, C=AT
TimeStamper    : CN=Microsoft Public RSA Time Stamping Authority
ProductVersion : 2.2.0.0
```

Re-verify at any time — the signature is what makes this binary trustworthy, not the fact that it
sits in this repository:

```powershell
Get-AuthenticodeSignature .\PawnIO_setup.exe | Format-List Status, SignerCertificate
```

Any modification to this file, including a line-ending rewrite, invalidates that signature and
Windows will refuse to load the driver. `.gitattributes` marks `*.exe` as binary for this reason.

## Licence

PawnIO is licensed under the **GNU General Public License version 2** (full text in `COPYING`) with
the following special exception, quoted verbatim from the upstream `README.md`:

> In addition, as a special exception, the copyright holders of PawnIO give you permission to
> combine PawnIO program with free software programs or libraries that are released under the GNU
> LGPL and with independent modules that communicate with PawnIO solely through the device IO
> control interface. You may copy and distribute such a system following the terms of the GNU GPL
> for PawnIO and the licenses of the other code concerned, provided that you include the source code
> of that other code when and as the GNU GPL requires distribution of source code.
>
> Note that this exception does not include programs that communicate with PawnIO over the Pawn
> interface. This means that all modules loaded into PawnIO must be compatible with this licence,
> including the earlier exception clause. We recommend using the GNU Lesser General Public License
> version 2.1 to fulfill this requirement.

Copyright © 2026 namazso <admin@namazso.eu>. For alternative licensing options, contact the
copyright holder at that address.

### What this means for this project

Cfa835SystemMonitor talks to PawnIO **solely through the device IO control interface**, by way of
`LibreHardwareMonitorLib`. It never loads a module over the Pawn interface. The special exception
therefore applies, and this project's own MIT licence is unaffected by redistributing the driver.

Redistributing the binary triggers GPLv2 §3, which requires the corresponding source to accompany
it. `PawnIO-2.2.0-source.zip` is that source, shipped under §3(a); no separate written offer is
needed. Keep the three files together whenever this driver is redistributed.

## Updating

1. Download the new `PawnIO_setup.exe` from <https://github.com/namazso/PawnIO.Setup/releases>.
2. Verify its Authenticode signature and confirm the signer is still `CN=namazso.eu`.
3. Download the matching source tag from <https://github.com/namazso/PawnIO> and replace the archive
   and `COPYING`. The source must correspond to the binary, not merely be the newest commit.
4. Update the hashes, sizes, version, and commit id in the table above.
5. Update the version stated in `README.md`, `DEPLOYMENT.md`, `CLAUDE.md`, and
   `THIRD-PARTY-NOTICES.md`.

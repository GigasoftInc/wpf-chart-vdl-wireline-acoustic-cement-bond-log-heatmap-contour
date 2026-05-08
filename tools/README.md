# Tools — Data Regeneration

This folder contains the Python script used to generate
`TestData_FORGE_VDL.txt` from the original Schlumberger DLIS source.

**You do not need any of this to run the demo.** The bundled test data
file is checked in and the C# project loads it directly. This folder
exists so that:

- the data lineage is auditable (anyone can verify that the demo data
  came from the cited DLIS file without alteration), and
- the conversion is reproducible for other channels, depth ranges,
  or wells.

---

## Prerequisites

- Python 3.9 or later
- Two pip packages:

  ```bash
  pip install dlisio numpy
  ```

  Add `lasio` if you also want to process LAS files:

  ```bash
  pip install lasio
  ```

The script has no other runtime dependencies.

---

## Source Data

The bundled `TestData_FORGE_VDL.txt` was generated from the **Cement
Bond Log** DLIS file in the Utah FORGE 16A(78)-32 dataset:

- GDR submission: <https://gdr.openei.org/submissions/1330>
- DOI: [10.15121/1814488](https://gdr.openei.org/submissions/1330)
- Licence: Creative Commons Attribution 4.0 International (CC-BY 4.0)
- Citation: McLennan 2021

The relevant file inside the submission is the CBL DLIS (~95 MB on
disk). Download it from the GDR page above; the script reads it
directly without unpacking.

---

## Listing Channels

Before converting, run `--list` to see what's inside any DLIS or LAS
file:

```bash
python convert_log_data.py path/to/CBL.dlis --list
```

The DLIS lister enumerates every logical file, frame, and channel,
showing each channel's dimensions and units. Look for an array-shaped
channel — VDL data is stored as `[n_depths, n_time_samples]`, typically
`[n, 256]` for Schlumberger CBL/QSLT tools.

LAS files use a different convention — array waveforms are stored as
parallel scalar curves named `VDL[1]`, `VDL[2]`, ... `VDL[256]`. The
LAS lister groups these automatically so you see one entry per array
rather than 256 individual columns.

---

## Regenerating the Bundled Demo File

The exact command that produced `TestData_FORGE_VDL.txt`:

```bash
python convert_log_data.py path/to/CBL.dlis VDL \
    --depth-range 3000 3500 \
    --depth-stride 1 \
    --out TestData_FORGE_VDL.txt
```

This crops to the cemented casing interval (3000–3500 ft, sealed cement
section), keeps every depth row at the source 0.125 ft resolution, and
normalises amplitude to a 0–100 range with the 99.5%-ile saturation
default. The result is 4001 depths × 256 time samples ≈ 50 MB on disk.

For a smaller demo file suitable for a public repo without the LFS-size
overhead:

```bash
python convert_log_data.py path/to/CBL.dlis VDL \
    --depth-range 3000 3500 \
    --depth-stride 4 \
    --out TestData_FORGE_VDL.txt
```

`--depth-stride 4` keeps every fourth depth row, dropping the file from
~50 MB to ~12 MB. The contour-injection technique still works identically
on the smaller grid.

---

## Useful Flags

| Flag | Effect |
|------|--------|
| `--list` | Enumerate channels and exit (no output written). |
| `--frame NAME` | DLIS only: read the channel from a specific frame. |
| `--out PATH` | Output filename (default: `TestData.txt`). |
| `--depth-range LO HI` | Crop to depth interval, in source units. |
| `--depth-stride N` | Keep every Nth depth row. Use 4 or 16 for smaller demo files. |
| `--time-stride N` | Keep every Nth time sample. |
| `--sample-us VALUE` | Override the time sample interval. The script tries to read it from DLIS metadata (`WSR` / `WAVE_SAMPLE_RATE` / `SR` parameters); falls back to 6 µs if absent. |
| `--raw` | Skip the 0–100 normalisation and write source amplitudes verbatim. |

---

## Output Format

The C# loader (`LoadVdlTestData` in `Mainwindow.xaml.cs`) expects the
following exact format. The script writes it byte-for-byte:

- Encoding: UTF-16 LE with BOM (`FF FE`)
- Line endings: CRLF
- Field separator: tab
- Three columns: `time(µs)` &nbsp; `depth(ft)` &nbsp; `amplitude`
- Empty time field when `time == 0` (start of waveform)
- Empty amplitude field for null cells
- Ordering: outer loop = depth, inner loop = time

This format predates the project — it matches the original test data
file shipped by Gigasoft for ProEssentials VDL examples. Keeping the
exact format means the demo can drop in any conforming file without C#
changes.

---

## Adapting to Other Wells

The script is not FORGE-specific. Any DLIS file with a 2D array channel
indexed by depth and a sibling depth channel (TDEP / DEPT / DEPTH / MD)
will load. Schlumberger CBL, QSLT, and CBT tools all produce compatible
arrays under various channel names — `VDL`, `WF1`, `WAVS`, depending on
the tool generation. Run `--list` first to find the channel name in
your file.

For LAS input, point the script at the array's base name and it
auto-stacks the columns:

```bash
python convert_log_data.py path/to/well.las VDL --out TestData.txt
```

---

## Licence

This script is MIT licensed, same as the rest of the example code.
The data it processes is the user's responsibility — the FORGE
dataset above is CC-BY 4.0 (attribution required); proprietary client
DLIS files retain whatever licence terms govern the original.

"""
Convert DLIS or LAS well-log files into TestData.txt format readable by the
ProEssentials VDL colormap demo.

Tested with the Utah FORGE Well 78B-32 CBL and QSLT Sonic datasets:
    https://gdr.openei.org/submissions/1330            (DOI 10.15121/1814488)
    Licence: CC-BY 4.0  (just credit the source in your repo's README)

Usage:
    # 1.  List every channel/curve in a file (no output written):
    python3 convert_log_data.py path/to/file.dlis --list
    python3 convert_log_data.py path/to/file.las   --list

    # 2.  Pick a channel and convert.  For DLIS, supply the channel name
    #     (e.g. VDL, WF1).  For LAS, supply either a single array-curve
    #     base name (e.g. VDL  -> auto-stacks VDL[1]..VDL[N]) or "auto".
    python3 convert_log_data.py path/to/file.dlis VDL --out TestData.txt

    # 3.  Useful subsampling flags (the FORGE CBL has ~16k depths --
    #     too big for a public repo; subsample to ~1000 for the demo):
    python3 convert_log_data.py file.dlis VDL --depth-stride 16 --time-stride 2 \\
        --depth-range 5000 6500 --out TestData.txt

Output format (matches the original customer-supplied TestData.txt exactly):
    UTF-16 LE with BOM, tab-separated, CRLF line endings.
    Columns: time(us)   depth(ft)   amplitude
    First column blank when time == 0.  Third column blank for null amp.
    Order: outer = depth, inner = time.

Dependencies:
    pip install dlisio lasio numpy
"""

import argparse
import sys
from pathlib import Path

import numpy as np


# =========================================================================
#  Output format helpers (independent of input format)
# =========================================================================
def write_testdata(path: Path, depths, times, amplitude, valid):
    """
    Write our standard TestData.txt format.
    depths: 1D array of depth values
    times:  1D array of time values
    amplitude: 2D array [n_depths, n_times]
    valid:  2D bool array [n_depths, n_times] (False -> null)
    """
    if amplitude.shape != (len(depths), len(times)):
        raise ValueError(
            f"amplitude shape {amplitude.shape} doesn't match "
            f"(n_depths={len(depths)}, n_times={len(times)})")

    with open(path, "wb") as f:
        f.write(b"\xff\xfe")                       # UTF-16 LE BOM
        for di, depth in enumerate(depths):
            for ti, t in enumerate(times):
                t_str = "" if t == 0.0 else f"{t:.3f}"
                d_str = f"{depth:.3f}"
                if valid[di, ti] and np.isfinite(amplitude[di, ti]):
                    a_str = f"{amplitude[di, ti]:.3f}"
                else:
                    a_str = " "
                line = f"{t_str}\t{d_str}\t{a_str}\r\n"
                f.write(line.encode("utf-16-le"))


def normalize_amplitude(amp, target_max=100.0):
    """
    Map raw amplitudes into [0, target_max] using the 99.5 percentile of
    |amp| as the saturation point.  Most VDL renderings use a fixed
    saturation so a few extreme outliers don't compress everything else
    into a narrow band of colour.

    Negative inputs (rectified VDL data is sometimes signed) are returned
    as their absolute value first, since stacked-bar colormaps are most
    legible with non-negative amplitudes.
    """
    finite = np.isfinite(amp)
    work = np.where(finite, np.abs(amp), 0.0)
    if not np.any(finite):
        return work, finite
    p995 = np.percentile(work[finite], 99.5)
    if p995 <= 0:
        return work, finite
    scaled = np.clip(work / p995 * target_max, 0.0, target_max)
    scaled[~finite] = 0.0
    return scaled, finite


# =========================================================================
#  DLIS reader
# =========================================================================
def list_dlis_channels(path):
    import dlisio.dlis as dlis
    print(f"\n=== DLIS file: {path} ===\n")
    with dlis.load(str(path)) as files:
        for fi, f in enumerate(files):
            print(f"-- Logical file {fi}: {f}")
            for fr in f.frames:
                print(f"   Frame: {fr.name}   index_type={fr.index_type}")
                for ch in fr.channels:
                    dims = ch.dimension
                    units = ch.units or ""
                    print(f"     Channel: {ch.name:<12s}  dims={dims}  "
                          f"units={units!r:<10s}  long={ch.long_name!s:.60s}")
    print()


def _depth_to_feet(values, units):
    """
    Schlumberger DLIS depth channels are commonly stored in '0.1 in' units
    (the raw tool encoder) or in 'in' or 'm'.  Convert anything we
    recognise to feet so downstream code has a single unit.
    """
    u = (units or "").strip().lower()
    if u in ("ft", "feet", "foot"):
        return np.asarray(values, dtype=np.float64), "ft"
    if u in ("0.1 in", "0.1in", "0.1 inch"):
        return np.asarray(values, dtype=np.float64) / 120.0, "ft"
    if u in ("in", "inch", "inches"):
        return np.asarray(values, dtype=np.float64) / 12.0, "ft"
    if u in ("m", "meter", "meters", "metre", "metres"):
        return np.asarray(values, dtype=np.float64) * 3.280839895, "ft"
    # Unknown unit -- pass through, warn
    print(f"WARNING: unknown depth units {units!r}; passing values through "
          f"unchanged.  Use --depth-units to override.", file=sys.stderr)
    return np.asarray(values, dtype=np.float64), units or ""


def load_dlis_channel(path, channel_name, frame_name=None):
    """
    Load (depths, times, amplitude_grid, valid_mask) for the named array channel.

    Returns:
      depths:    1D numpy array of feet, length n_depths, SHALLOW->DEEP
      times:     1D numpy array, length n_samples_per_waveform (microseconds)
      amplitude: 2D numpy array [n_depths, n_samples] reordered to match depths
      valid:     2D bool array, True where amplitude is finite
      units:     channel units string
    """
    import dlisio.dlis as dlis

    with dlis.load(str(path)) as files:
        for f in files:
            for fr in f.frames:
                if frame_name and fr.name != frame_name:
                    continue
                # Find the requested channel in this frame
                target = None
                for ch in fr.channels:
                    if ch.name == channel_name:
                        target = ch
                        break
                if target is None:
                    continue

                amp = target.curves()
                if amp.ndim == 1:
                    raise ValueError(
                        f"Channel '{channel_name}' has scalar samples "
                        f"(shape {amp.shape}). VDL needs an array channel.")
                if amp.ndim != 2:
                    raise ValueError(
                        f"Channel '{channel_name}' has unexpected ndim "
                        f"{amp.ndim} (shape {amp.shape}).")
                amp = np.asarray(amp, dtype=np.float64)
                n_depths, n_samples = amp.shape

                # Index channel = depth
                depth_channel = None
                for cand in ("TDEP", "DEPT", "DEPTH", "MD"):
                    for ch in fr.channels:
                        if ch.name == cand:
                            depth_channel = ch
                            break
                    if depth_channel is not None:
                        break
                if depth_channel is None:
                    depths = np.arange(n_depths, dtype=np.float64)
                    print("WARNING: no depth channel found, using sample indices",
                          file=sys.stderr)
                else:
                    raw_depths = depth_channel.curves()
                    depths, _ = _depth_to_feet(raw_depths, depth_channel.units)
                    if len(depths) != n_depths:
                        n = min(len(depths), n_depths)
                        depths = depths[:n]
                        amp = amp[:n]
                        n_depths = n

                # Wireline tools log uphole, so DLIS depth arrays are usually
                # deep-to-shallow.  Flip to shallow-to-deep for downstream
                # consistency.
                if n_depths >= 2 and depths[0] > depths[-1]:
                    depths = depths[::-1].copy()
                    amp    = amp[::-1].copy()

                # Time axis -- 6 us/sample default (matches QSLT/CBT for FORGE)
                sample_us = 6.0
                try:
                    for p in f.parameters:
                        if p.name in ("WSR", "WAVE_SAMPLE_RATE", "SR"):
                            v = p.values
                            if hasattr(v, "__len__") and len(v) > 0:
                                sample_us = float(v[0])
                                break
                except Exception:
                    pass
                times = np.arange(n_samples, dtype=np.float64) * sample_us

                valid = np.isfinite(amp)
                valid &= (np.abs(amp) < 1e30)
                return depths, times, amp, valid, target.units or ""

    raise ValueError(
        f"Channel '{channel_name}' not found in {path}"
        + (f" (frame {frame_name})" if frame_name else ""))


# =========================================================================
#  LAS reader
# =========================================================================
def list_las_curves(path):
    import lasio
    print(f"\n=== LAS file: {path} ===\n")
    las = lasio.read(str(path))
    print(f"Wells: {las.well!s:.200s}")
    print(f"Curves ({len(las.curves)}):")

    # Group array-style curves (NAME[1], NAME[2], ...) so the listing
    # is readable when there are 256 VDL columns.
    groups = {}
    scalars = []
    for c in las.curves:
        m = _array_curve_re().match(c.mnemonic)
        if m:
            base = m.group(1)
            idx = int(m.group(2))
            groups.setdefault(base, []).append(idx)
        else:
            scalars.append(c)

    for base, idxs in sorted(groups.items()):
        idxs.sort()
        print(f"  {base}[{idxs[0]}..{idxs[-1]}]  (array, {len(idxs)} entries)")
    for c in scalars:
        print(f"  {c.mnemonic:<12s}  unit={c.unit!r:<8s}  desc={c.descr!s:.50s}")
    print()


def _array_curve_re():
    import re
    return re.compile(r"^([A-Za-z][A-Za-z0-9_]*?)\[(\d+)\]$")


def load_las_array_channel(path, base_name):
    """
    Load an array-style LAS curve, e.g. VDL[1], VDL[2], ... VDL[N].
    Returns (depths, times, amplitude, valid, units).
    """
    import lasio
    las = lasio.read(str(path))
    rx = _array_curve_re()

    # Collect VDL[k] columns
    cols = []
    units = ""
    for c in las.curves:
        m = rx.match(c.mnemonic)
        if m and m.group(1).upper() == base_name.upper():
            cols.append((int(m.group(2)), c.mnemonic))
            if not units:
                units = c.unit or ""
    if not cols:
        raise ValueError(
            f"No array curves named '{base_name}[...]' in {path}. "
            f"Run with --list to see what's available.")
    cols.sort()
    n_samples = len(cols)

    depths = np.asarray(las.index, dtype=np.float64)
    n_depths = len(depths)
    amp = np.empty((n_depths, n_samples), dtype=np.float64)
    for ci, (_, mnem) in enumerate(cols):
        amp[:, ci] = np.asarray(las[mnem], dtype=np.float64)

    # Default sample rate -- override via --sample-us if needed.
    sample_us = 6.0
    times = np.arange(n_samples, dtype=np.float64) * sample_us

    valid = np.isfinite(amp)
    return depths, times, amp, valid, units


# =========================================================================
#  Main pipeline
# =========================================================================
def convert(args):
    path = Path(args.input)
    if not path.is_file():
        print(f"ERROR: file not found: {path}", file=sys.stderr)
        return 1

    suffix = path.suffix.lower()

    # --- list mode ---
    if args.list:
        if suffix == ".dlis":
            list_dlis_channels(path)
        elif suffix == ".las":
            list_las_curves(path)
        else:
            print(f"Unknown extension {suffix!r}; expected .dlis or .las",
                  file=sys.stderr)
            return 1
        return 0

    if not args.channel:
        print("ERROR: pass a channel name (or --list to see options)",
              file=sys.stderr)
        return 1

    # --- load ---
    if suffix == ".dlis":
        depths, times, amp, valid, units = load_dlis_channel(
            path, args.channel, frame_name=args.frame)
    elif suffix == ".las":
        depths, times, amp, valid, units = load_las_array_channel(
            path, args.channel)
    else:
        print(f"Unknown extension {suffix!r}", file=sys.stderr)
        return 1

    print(f"Loaded channel '{args.channel}': {amp.shape[0]} depths x "
          f"{amp.shape[1]} samples   units={units!r}")
    print(f"  depth range: {depths.min():.2f} .. {depths.max():.2f}")
    print(f"  time range:  {times.min():.2f} .. {times.max():.2f} us")
    print(f"  amplitude:   min={amp[valid].min():.3f}  max={amp[valid].max():.3f}  "
          f"valid={valid.sum()}/{valid.size}")

    # Override sample rate / regenerate times if user asked
    if args.sample_us is not None:
        times = np.arange(amp.shape[1], dtype=np.float64) * args.sample_us
        print(f"  (overriding sample rate -> times now 0..{times.max():.2f} us)")

    # --- depth range crop ---
    if args.depth_range:
        lo, hi = args.depth_range
        mask = (depths >= lo) & (depths <= hi)
        if not mask.any():
            print(f"ERROR: no depths in range {lo}..{hi}", file=sys.stderr)
            return 1
        depths = depths[mask]
        amp    = amp[mask]
        valid  = valid[mask]
        print(f"  depth-range crop -> {len(depths)} depths "
              f"({depths.min():.2f}..{depths.max():.2f})")

    # --- subsample ---
    if args.depth_stride > 1:
        depths = depths[::args.depth_stride]
        amp    = amp[::args.depth_stride]
        valid  = valid[::args.depth_stride]
    if args.time_stride > 1:
        times = times[::args.time_stride]
        amp   = amp[:, ::args.time_stride]
        valid = valid[:, ::args.time_stride]
    print(f"  after stride -> {amp.shape[0]} depths x {amp.shape[1]} times")

    # --- normalise to 0..100 (matches the C# demo's color spec) ---
    if not args.raw:
        amp, finite = normalize_amplitude(amp, target_max=100.0)
        valid = valid & finite
        print(f"  normalised to 0..100 (99.5%-ile saturation)")

    # --- write ---
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    write_testdata(out, depths, times, amp, valid)
    size_mb = out.stat().st_size / 1024 / 1024
    print(f"\nWrote {out}  ({size_mb:.1f} MB)")
    return 0


def main():
    p = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("input", help="path to .dlis or .las file")
    p.add_argument("channel", nargs="?", default=None,
                   help="channel name (e.g. VDL, WF1).  Omit when using --list.")
    p.add_argument("--list", action="store_true",
                   help="list channels/curves and exit")
    p.add_argument("--frame", default=None,
                   help="DLIS only: name of frame to read from "
                        "(default: first frame containing the channel)")
    p.add_argument("--out", default="TestData.txt",
                   help="output path (default: TestData.txt)")
    p.add_argument("--depth-stride", type=int, default=1,
                   help="keep every Nth depth (default 1).  Use 16 to take a "
                        "16x-subsampled VDL down from ~16k to ~1k depths.")
    p.add_argument("--time-stride", type=int, default=1,
                   help="keep every Nth time sample (default 1)")
    p.add_argument("--depth-range", type=float, nargs=2, metavar=("LO", "HI"),
                   help="restrict to depth interval [LO, HI] (in source units)")
    p.add_argument("--sample-us", type=float, default=None,
                   help="override sample interval in microseconds "
                        "(default: tries to read from file metadata, falls "
                        "back to 6 us)")
    p.add_argument("--raw", action="store_true",
                   help="don't normalise amplitudes (default: scale to 0..100)")
    args = p.parse_args()

    return convert(args)


if __name__ == "__main__":
    sys.exit(main())

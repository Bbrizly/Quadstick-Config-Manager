/**
 * Percent of half-scale from the calibrated zero (DataFlow.c:294-297). 95, not
 * 100: at 100 the threshold meets the maximum held for the test. Same numbers
 * and search as `qcm_core::console::strength_sweep`.
 */
export const SWEEP_LOWEST = 5;
export const SWEEP_HIGHEST = 95;

/**
 * The highest threshold the person reaches, by binary search: 7 tries cover 5
 * to 95. `lo - 1` means they reached none of it. One try per step, so a weak
 * breath on one try reads low, as in the shipped app.
 */
export async function strengthSweep(
  reaches: (threshold: number) => Promise<boolean>,
  lo: number = SWEEP_LOWEST,
  hi: number = SWEEP_HIGHEST,
): Promise<number> {
  let best = lo - 1;
  while (lo <= hi) {
    const mid = Math.floor((lo + hi + 1) / 2);
    // oxlint-disable-next-line no-await-in-loop -- each breath decides the next threshold
    if (await reaches(mid)) {
      best = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return best;
}

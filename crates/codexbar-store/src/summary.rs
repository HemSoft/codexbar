//! Summaries and chart series over one metric's stored history, for the compact trend and the History view (#86).
//! Pure functions over samples, so empty, single-sample, flat, spiking and gappy series are tested directly.

use chrono::{DateTime, Duration, Utc};

/// One reading: fraction used for limits, dollars for balances.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Point {
    pub at: DateTime<Utc>,
    pub value: f64,
}

impl Point {
    pub fn new(at: DateTime<Utc>, value: f64) -> Self {
        Self { at, value }
    }
}

/// What a history range says at a glance: where it ended, how it moved, its spread, and what it is based on.
#[derive(Clone, Debug, PartialEq)]
pub struct HistorySummary {
    pub latest: f64,
    pub latest_at: DateTime<Utc>,
    /// `latest` minus the first value in the range; zero for a single sample.
    pub change: f64,
    pub low: f64,
    pub high: f64,
    pub samples: usize,
    pub first_at: DateTime<Utc>,
    /// The longest stretch between two samples, when it is long enough to be missing data rather than sampling rhythm.
    pub longest_gap: Option<Duration>,
    /// How long nothing has been recorded since `latest_at`, when that is long enough to mean the data stopped.
    pub stale_for: Option<Duration>,
}

impl HistorySummary {
    /// The time the samples span; zero for a single sample.
    pub fn span(&self) -> Duration {
        self.latest_at - self.first_at
    }

    /// True when the values never moved by more than the store treats as a change.
    pub fn is_flat(&self) -> bool {
        self.high - self.low < FLAT_EPSILON
    }
}

/// Unchanged values are re-sampled every 15 minutes, so a quiet hour means the app wasn't running or fetching.
pub const GAP_THRESHOLD: Duration = Duration::hours(1);
/// Differences smaller than this are not changes; matches the history store's deduplication.
pub const FLAT_EPSILON: f64 = 0.0005;

/// Summarizes points sorted oldest first, as of `end` (normally now). `None` when there are none, so callers show an
/// empty state instead of zeros. Non-finite values are ignored.
pub fn summarize(points: &[Point], end: DateTime<Utc>) -> Option<HistorySummary> {
    let mut finite = points.iter().filter(|point| point.value.is_finite());
    let first = *finite.next()?;
    let mut summary = HistorySummary {
        latest: first.value,
        latest_at: first.at,
        change: 0.0,
        low: first.value,
        high: first.value,
        samples: 1,
        first_at: first.at,
        longest_gap: None,
        stale_for: None,
    };
    let mut longest = Duration::zero();
    for point in finite {
        longest = longest.max(point.at - summary.latest_at);
        summary.latest = point.value;
        summary.latest_at = point.at;
        summary.low = summary.low.min(point.value);
        summary.high = summary.high.max(point.value);
        summary.samples += 1;
    }
    summary.change = summary.latest - first.value;
    summary.longest_gap = (longest > GAP_THRESHOLD).then_some(longest);
    let since_latest = end - summary.latest_at;
    summary.stale_for = (since_latest > GAP_THRESHOLD).then_some(since_latest);
    Some(summary)
}

/// A point to draw. `measured` points are real readings; the others fill a gap with the straight line between the
/// readings on either side, so missing time keeps its width on the chart instead of being squeezed out.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ChartPoint {
    pub at: DateTime<Utc>,
    pub value: f64,
    pub measured: bool,
}

/// Points to draw, evenly spaced in time. A short series without gaps is drawn as is. Otherwise the span is split
/// into `max_points / 2` equal time buckets of two points each: a bucket with readings keeps its lowest and highest
/// in time order, so both spikes and resets survive; an empty bucket gets two unmeasured points on the line between
/// its neighbors. The first and last readings are always kept.
pub fn chart_series(points: &[Point], max_points: usize) -> Vec<ChartPoint> {
    let finite: Vec<Point> = points.iter().copied().filter(|point| point.value.is_finite()).collect();
    let measured = |point: &Point| ChartPoint {
        at: point.at,
        value: point.value,
        measured: true,
    };
    let has_gap = finite.windows(2).any(|pair| pair[1].at - pair[0].at > GAP_THRESHOLD);
    let buckets = (max_points / 2).max(2);
    if finite.len() <= max_points && !has_gap {
        return finite.iter().map(measured).collect();
    }
    let (first, last) = (finite[0], finite[finite.len() - 1]);
    let start = first.at.timestamp();
    let span = (last.at.timestamp() - start).max(1);
    let bucket_of = |point: &Point| {
        let offset = (point.at.timestamp() - start) as i128 * buckets as i128 / (span as i128 + 1);
        (offset as usize).min(buckets - 1)
    };

    // Lowest and highest reading per bucket.
    let mut extremes: Vec<Option<(Point, Point)>> = vec![None; buckets];
    for point in &finite {
        let slot = &mut extremes[bucket_of(point)];
        *slot = Some(match *slot {
            None => (*point, *point),
            Some((low, high)) => (
                if point.value < low.value { *point } else { low },
                if point.value > high.value { *point } else { high },
            ),
        });
    }
    // The chart starts and ends on real readings: the first and last replace the nearer extreme of their buckets.
    for (ix, keep) in [(0, first), (buckets - 1, last)] {
        if let Some((low, high)) = extremes[ix].as_mut()
            && low.at != keep.at
            && high.at != keep.at
        {
            if keep.value - low.value <= high.value - keep.value {
                *low = keep;
            } else {
                *high = keep;
            }
        }
    }

    let bucket_time = |ix: usize, quarter: i64| {
        let seconds = (span as i128 + 1) * (ix as i128 * 4 + quarter as i128) / (buckets as i128 * 4);
        first.at + Duration::seconds(seconds as i64)
    };
    let mut out = Vec::with_capacity(buckets * 2);
    for ix in 0..buckets {
        match extremes[ix] {
            Some((low, high)) => {
                let (a, b) = if low.at <= high.at { (low, high) } else { (high, low) };
                out.push(measured(&a));
                out.push(ChartPoint {
                    measured: b.at != a.at,
                    ..measured(&b)
                });
            }
            None => {
                // Empty buckets lie strictly between the first and last, so both neighbors exist.
                let before = out.last().copied().unwrap_or_else(|| measured(&first));
                let after = extremes[ix + 1..]
                    .iter()
                    .flatten()
                    .map(|(low, high)| if low.at <= high.at { *low } else { *high })
                    .next()
                    .unwrap_or(last);
                for quarter in [1, 3] {
                    let at = bucket_time(ix, quarter);
                    let whole = (after.at - before.at).num_seconds().max(1) as f64;
                    let part = (at - before.at).num_seconds() as f64 / whole;
                    out.push(ChartPoint {
                        at,
                        value: before.value + (after.value - before.value) * part.clamp(0.0, 1.0),
                        measured: false,
                    });
                }
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    fn at(minutes: i64) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 10, 1, 0, 0, 0).unwrap() + Duration::minutes(minutes)
    }

    fn points(values: &[(i64, f64)]) -> Vec<Point> {
        values
            .iter()
            .map(|&(minutes, value)| Point::new(at(minutes), value))
            .collect()
    }

    fn summary_at(values: &[(i64, f64)], end_minutes: i64) -> HistorySummary {
        summarize(&points(values), at(end_minutes)).unwrap()
    }

    #[test]
    fn summarize_empty_is_none() {
        assert_eq!(summarize(&[], at(0)), None);
        assert_eq!(summarize(&points(&[(0, f64::NAN)]), at(0)), None);
    }

    #[test]
    fn summarize_single_sample_has_no_change_or_span() {
        let summary = summary_at(&[(0, 0.4)], 0);
        assert_eq!(summary.latest, 0.4);
        assert_eq!(summary.change, 0.0);
        assert_eq!((summary.low, summary.high), (0.4, 0.4));
        assert_eq!(summary.samples, 1);
        assert_eq!(summary.span(), Duration::zero());
        assert!(summary.is_flat());
    }

    #[test]
    fn summarize_flat_series_is_flat_within_the_store_tolerance() {
        let summary = summary_at(&[(0, 0.2), (15, 0.2001), (30, 0.2)], 30);
        assert!(summary.is_flat(), "a change the store wouldn't record is not a change");
        assert_eq!(summary.longest_gap, None);
        assert!(!summary_at(&[(0, 0.2), (15, 0.21)], 15).is_flat());
    }

    #[test]
    fn summarize_spike_keeps_high_and_change_from_first() {
        let summary = summary_at(&[(0, 0.1), (15, 0.9), (30, 0.3)], 30);
        assert_eq!(summary.high, 0.9);
        assert_eq!(summary.low, 0.1);
        assert!((summary.change - 0.2).abs() < 1e-9);
        assert_eq!(summary.latest, 0.3);
        assert_eq!(summary.span(), Duration::minutes(30));
    }

    #[test]
    fn summarize_reports_missing_data_gaps_but_not_sampling_rhythm() {
        let summary = summary_at(&[(0, 0.1), (15, 0.1), (315, 0.5), (330, 0.6)], 330);
        assert_eq!(summary.longest_gap, Some(Duration::minutes(300)));
        let rhythm = summary_at(&[(0, 0.1), (60, 0.2), (120, 0.3)], 120);
        assert_eq!(rhythm.longest_gap, None, "an hour apart is not a gap");
    }

    #[test]
    fn summarize_reports_data_that_stopped_arriving() {
        let summary = summary_at(&[(0, 0.1), (15, 0.2)], 15 + 6 * 60);
        assert_eq!(summary.stale_for, Some(Duration::hours(6)));
        assert_eq!(summary_at(&[(0, 0.1), (15, 0.2)], 30).stale_for, None);
    }

    #[test]
    fn summarize_ignores_non_finite_values() {
        let summary = summary_at(&[(0, 0.1), (15, f64::INFINITY), (30, 0.3)], 30);
        assert_eq!(summary.samples, 2);
        assert_eq!(summary.high, 0.3);
    }

    fn values(series: &[ChartPoint]) -> Vec<f64> {
        series.iter().map(|point| point.value).collect()
    }

    #[test]
    fn chart_series_draws_short_regular_series_as_is() {
        let series = chart_series(&points(&[(0, 0.1), (15, f64::NAN), (30, 0.3)]), 120);
        assert_eq!(values(&series), vec![0.1, 0.3]);
        assert!(series.iter().all(|point| point.measured));
        assert!(chart_series(&[], 120).is_empty());
    }

    #[test]
    fn chart_series_keeps_peaks_and_troughs_of_a_sawtooth() {
        // A 5-hour window sampled every 15 minutes for 30 days: climbs to 0.9, resets to 0.
        let samples: Vec<(i64, f64)> = (0..30 * 24 * 4)
            .map(|ix| (ix * 15, (ix % 20) as f64 / 19.0 * 0.9))
            .collect();
        let series = chart_series(&points(&samples), 120);
        assert_eq!(series.len(), 120, "two points per bucket");
        let (low, high) = series.iter().fold((f64::MAX, f64::MIN), |(low, high), point| {
            (low.min(point.value), high.max(point.value))
        });
        assert_eq!(low, 0.0, "resets survive");
        assert!((high - 0.9).abs() < 1e-9, "peaks survive");
        let lows = series.iter().filter(|point| point.value < 0.1).count();
        assert!(lows >= 50, "nearly every bucket shows its reset, not just its peak");
    }

    #[test]
    fn chart_series_keeps_a_short_spike() {
        let mut samples: Vec<(i64, f64)> = (0..1000).map(|ix| (ix, 0.1)).collect();
        samples[503].1 = 0.95;
        let series = chart_series(&points(&samples), 50);
        assert!(series.iter().any(|point| point.value == 0.95));
        assert_eq!(series.first().unwrap().at, at(0));
        assert_eq!(series.last().unwrap().at, at(999));
        assert_eq!(series.first().unwrap().value, 0.1);
    }

    #[test]
    fn chart_series_keeps_gaps_their_width_without_inventing_readings() {
        // Readings for 100 minutes, nothing for 800, readings again for 100.
        let mut samples: Vec<(i64, f64)> = (0..100).map(|ix| (ix, 0.2)).collect();
        samples.extend((900..1000).map(|ix| (ix, 0.4)));
        let series = chart_series(&points(&samples), 40);
        assert_eq!(series.len(), 40);
        // Evenly spaced: the 800 empty minutes take 16 of the 20 buckets, as they take 80% of the time.
        let in_gap: Vec<&ChartPoint> = series
            .iter()
            .filter(|point| point.at > at(99) && point.at < at(900))
            .collect();
        assert_eq!(in_gap.len(), 32, "two points per empty bucket");
        assert!(
            in_gap.iter().all(|point| !point.measured),
            "nothing in the gap claims to be a reading"
        );
        // Gap points lie on the line between the readings either side, rising steadily.
        assert!(in_gap.iter().all(|point| (0.2..=0.4).contains(&point.value)));
        assert!(in_gap.windows(2).all(|pair| pair[1].value >= pair[0].value));
    }

    #[test]
    fn chart_series_spaces_a_small_gappy_series_by_time() {
        let series = chart_series(&points(&[(0, 0.1), (15, 0.2), (600, 0.3)]), 20);
        assert_eq!(series.len(), 20);
        assert_eq!(series.iter().filter(|point| point.measured).count(), 3);
    }
}

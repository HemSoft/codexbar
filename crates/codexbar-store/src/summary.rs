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
    /// The longest stretch without samples, when it is long enough to be missing data rather than sampling rhythm.
    pub longest_gap: Option<Duration>,
}

impl HistorySummary {
    /// The time the samples span; zero for a single sample.
    pub fn span(&self) -> Duration {
        self.latest_at - self.first_at
    }

    /// True when every sample has the same value.
    pub fn is_flat(&self) -> bool {
        (self.high - self.low).abs() < f64::EPSILON
    }
}

/// Unchanged values are re-sampled every 15 minutes, so a quiet hour means the app wasn't running or fetching.
pub const GAP_THRESHOLD: Duration = Duration::hours(1);

/// Summarizes points sorted oldest first. `None` when there are none, so callers show an empty state instead of
/// zeros. Non-finite values are ignored.
pub fn summarize(points: &[Point]) -> Option<HistorySummary> {
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
    Some(summary)
}

/// Reduces points to at most `max_points` (minimum 3) for drawing. The first and last readings are kept as they are;
/// between them each time bucket keeps its highest value, so a short spike stays visible. Buckets without samples
/// are skipped rather than drawn as zero.
pub fn chart_points(points: &[Point], max_points: usize) -> Vec<Point> {
    let finite: Vec<Point> = points.iter().copied().filter(|point| point.value.is_finite()).collect();
    let max_points = max_points.max(3);
    if finite.len() <= max_points {
        return finite;
    }
    let (first, last) = (finite[0], finite[finite.len() - 1]);
    let start = first.at.timestamp();
    let span = (last.at.timestamp() - start).max(1) as f64;
    let mut buckets: Vec<Option<Point>> = vec![None; max_points - 2];
    for point in &finite[1..finite.len() - 1] {
        let offset = (point.at.timestamp() - start) as f64 / span;
        let ix = ((offset * buckets.len() as f64) as usize).min(buckets.len() - 1);
        match &buckets[ix] {
            Some(kept) if kept.value >= point.value => {}
            _ => buckets[ix] = Some(*point),
        }
    }
    std::iter::once(first)
        .chain(buckets.into_iter().flatten())
        .chain(std::iter::once(last))
        .collect()
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

    #[test]
    fn summarize_empty_is_none() {
        assert_eq!(summarize(&[]), None);
        assert_eq!(summarize(&points(&[(0, f64::NAN)])), None);
    }

    #[test]
    fn summarize_single_sample_has_no_change_or_span() {
        let summary = summarize(&points(&[(0, 0.4)])).unwrap();
        assert_eq!(summary.latest, 0.4);
        assert_eq!(summary.change, 0.0);
        assert_eq!((summary.low, summary.high), (0.4, 0.4));
        assert_eq!(summary.samples, 1);
        assert_eq!(summary.span(), Duration::zero());
        assert!(summary.is_flat());
    }

    #[test]
    fn summarize_flat_series_is_flat() {
        let summary = summarize(&points(&[(0, 0.2), (15, 0.2), (30, 0.2)])).unwrap();
        assert!(summary.is_flat());
        assert_eq!(summary.change, 0.0);
        assert_eq!(summary.longest_gap, None);
    }

    #[test]
    fn summarize_spike_keeps_high_and_change_from_first() {
        let summary = summarize(&points(&[(0, 0.1), (15, 0.9), (30, 0.3)])).unwrap();
        assert_eq!(summary.high, 0.9);
        assert_eq!(summary.low, 0.1);
        assert!((summary.change - 0.2).abs() < 1e-9);
        assert_eq!(summary.latest, 0.3);
        assert_eq!(summary.span(), Duration::minutes(30));
    }

    #[test]
    fn summarize_reports_missing_data_gaps_but_not_sampling_rhythm() {
        let summary = summarize(&points(&[(0, 0.1), (15, 0.1), (15 + 300, 0.5), (330, 0.6)])).unwrap();
        assert_eq!(summary.longest_gap, Some(Duration::minutes(300)));
        let rhythm = summarize(&points(&[(0, 0.1), (60, 0.2), (120, 0.3)])).unwrap();
        assert_eq!(rhythm.longest_gap, None, "an hour apart is not a gap");
    }

    #[test]
    fn summarize_ignores_non_finite_values() {
        let summary = summarize(&points(&[(0, 0.1), (15, f64::INFINITY), (30, 0.3)])).unwrap();
        assert_eq!(summary.samples, 2);
        assert_eq!(summary.high, 0.3);
    }

    #[test]
    fn chart_points_keeps_short_series_and_drops_non_finite() {
        let series = points(&[(0, 0.1), (15, f64::NAN), (30, 0.3)]);
        assert_eq!(chart_points(&series, 100), points(&[(0, 0.1), (30, 0.3)]));
        assert_eq!(chart_points(&[], 100), Vec::new());
    }

    #[test]
    fn chart_points_downsamples_keeping_spikes() {
        let mut values: Vec<(i64, f64)> = (0..1000).map(|ix| (ix, 0.1)).collect();
        values[503].1 = 0.95;
        let reduced = chart_points(&points(&values), 50);
        assert!(reduced.len() <= 50);
        assert!(
            reduced.iter().any(|point| point.value == 0.95),
            "the spike survives downsampling"
        );
        assert_eq!(reduced.first().unwrap().at, at(0));
        assert_eq!(reduced.last().unwrap().at, at(999));
    }

    #[test]
    fn chart_points_skips_empty_buckets_in_gaps() {
        let mut values: Vec<(i64, f64)> = (0..100).map(|ix| (ix, 0.2)).collect();
        values.extend((900..1000).map(|ix| (ix, 0.4)));
        let reduced = chart_points(&points(&values), 50);
        assert!(reduced.len() < 50, "the gap has no invented points");
        assert!(reduced.iter().all(|point| point.value == 0.2 || point.value == 0.4));
    }
}

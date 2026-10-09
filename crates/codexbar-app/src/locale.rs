//! Dates and times in the Windows user's own locale (#84): 12- or 24-hour time, local month and weekday names, and
//! the locale's own month-day order. Windows reads the user's current settings on every call, so a change in
//! Settings > Time & language shows on the next redraw. Tests use the fixed English style, so their output doesn't
//! depend on the machine running them.

use chrono::{Datelike as _, NaiveDate, NaiveDateTime, Timelike as _};
use codexbar_core::format::{DateStyle, English};

/// The style the app shows dates in.
pub fn style() -> &'static dyn DateStyle {
    if cfg!(test) { &English } else { &WindowsLocale }
}

/// The Windows user locale, through `GetDateFormatEx` and `GetTimeFormatEx`; English if a call fails.
pub struct WindowsLocale;

#[cfg(windows)]
mod win {
    use windows::Win32::Foundation::SYSTEMTIME;
    use windows::Win32::Globalization::{
        DATE_MONTHDAY, ENUM_DATE_FORMATS_FLAGS, GetDateFormatEx, GetTimeFormatEx, TIME_NOSECONDS,
    };
    use windows::core::{HSTRING, PCWSTR};

    fn read(buffer: &[u16], written: i32) -> Option<String> {
        // `written` counts the terminating NUL.
        (written > 1).then(|| String::from_utf16_lossy(&buffer[..written as usize - 1]))
    }

    /// A date with a format picture ("ddd") or, without one, a format flag (`DATE_MONTHDAY`).
    pub fn date(when: &SYSTEMTIME, flags: ENUM_DATE_FORMATS_FLAGS, picture: Option<&str>) -> Option<String> {
        let picture = picture.map(HSTRING::from);
        let format = picture.as_ref().map_or(PCWSTR::null(), |p| PCWSTR(p.as_ptr()));
        let mut buffer = [0u16; 80];
        // SAFETY: `when` and `buffer` outlive the call; a null locale name means the user default locale.
        let written = unsafe {
            GetDateFormatEx(
                PCWSTR::null(),
                flags,
                Some(when),
                format,
                Some(&mut buffer),
                PCWSTR::null(),
            )
        };
        read(&buffer, written)
    }

    pub fn time(when: &SYSTEMTIME) -> Option<String> {
        let mut buffer = [0u16; 40];
        // SAFETY: as above; a null format uses the locale's own time format.
        let written = unsafe {
            GetTimeFormatEx(
                PCWSTR::null(),
                TIME_NOSECONDS,
                Some(when),
                PCWSTR::null(),
                Some(&mut buffer),
            )
        };
        read(&buffer, written)
    }

    pub use windows::Win32::Globalization::ENUM_DATE_FORMATS_FLAGS as Flags;
    pub const NONE: Flags = ENUM_DATE_FORMATS_FLAGS(0);
    pub const MONTH_DAY: Flags = DATE_MONTHDAY;
}

#[cfg(windows)]
fn system_time(at: NaiveDateTime) -> windows::Win32::Foundation::SYSTEMTIME {
    windows::Win32::Foundation::SYSTEMTIME {
        wYear: at.year() as u16,
        wMonth: at.month() as u16,
        wDayOfWeek: at.weekday().num_days_from_sunday() as u16,
        wDay: at.day() as u16,
        wHour: at.hour() as u16,
        wMinute: at.minute() as u16,
        wSecond: 0,
        wMilliseconds: 0,
    }
}

#[cfg(windows)]
impl DateStyle for WindowsLocale {
    fn time(&self, at: NaiveDateTime) -> String {
        win::time(&system_time(at)).unwrap_or_else(|| English.time(at))
    }

    fn weekday(&self, at: NaiveDate) -> String {
        let at = at.and_hms_opt(12, 0, 0).unwrap_or_default();
        win::date(&system_time(at), win::NONE, Some("ddd")).unwrap_or_else(|| English.weekday(at.date()))
    }

    fn month_day(&self, at: NaiveDate) -> String {
        let at = at.and_hms_opt(12, 0, 0).unwrap_or_default();
        win::date(&system_time(at), win::MONTH_DAY, None).unwrap_or_else(|| English.month_day(at.date()))
    }
}

#[cfg(not(windows))]
impl DateStyle for WindowsLocale {
    fn time(&self, at: NaiveDateTime) -> String {
        English.time(at)
    }

    fn weekday(&self, at: NaiveDate) -> String {
        English.weekday(at)
    }

    fn month_day(&self, at: NaiveDate) -> String {
        English.month_day(at)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn thursday_nine() -> NaiveDateTime {
        NaiveDate::from_ymd_opt(2026, 10, 8)
            .unwrap()
            .and_hms_opt(9, 5, 0)
            .unwrap()
    }

    #[test]
    fn the_windows_locale_formats_every_part() {
        // Whatever this machine's locale, every part comes back non-empty and carries the right numbers.
        let at = thursday_nine();
        let time = WindowsLocale.time(at);
        assert!(time.contains('9') && time.contains("05"), "{time}");
        assert!(!WindowsLocale.weekday(at.date()).is_empty());
        let month_day = WindowsLocale.month_day(at.date());
        assert!(month_day.contains('8'), "{month_day}");
        assert!(WindowsLocale.full(at).contains(&month_day));
    }

    #[test]
    fn tests_use_the_fixed_english_style() {
        assert_eq!(style().weekday_time(thursday_nine()), "Thu 09:05");
        assert_eq!(style().month_day(thursday_nine().date()), "Oct 8");
    }
}

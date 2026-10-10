//! What a widget shows (#94): Adaptive Cards built from the dashboard's `widgets.json` snapshot, one per widget
//! configuration and size. Pure, so every state is tested without the widget host.

use chrono::{DateTime, Utc};
use codexbar_core::format::age_label;
use codexbar_store::widgets::{LoadError, WidgetAccount, WidgetHealth, WidgetSnapshot};
use serde_json::{Value, json};

/// A snapshot older than this means CodexBar isn't running: it rewrites the file at least every five minutes.
pub const NOT_RUNNING_AFTER: chrono::Duration = chrono::Duration::minutes(15);

/// Which accounts a widget shows.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub enum Focus {
    /// Every account, in dashboard order.
    #[default]
    All,
    /// One provider's accounts, by provider key.
    Provider(String),
    /// One group's accounts, by group id.
    Group(String),
}

impl Focus {
    pub fn key(&self) -> String {
        match self {
            Self::All => "all".into(),
            Self::Provider(key) => format!("provider:{key}"),
            Self::Group(id) => format!("group:{id}"),
        }
    }

    pub fn from_key(key: &str) -> Option<Self> {
        match key.split_once(':') {
            None if key == "all" => Some(Self::All),
            Some(("provider", key)) if !key.is_empty() => Some(Self::Provider(key.to_owned())),
            Some(("group", id)) if !id.is_empty() => Some(Self::Group(id.to_owned())),
            _ => None,
        }
    }
}

/// How many accounts a widget shows as tiles.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tiles {
    /// By widget size: one on small, two on medium, four on large.
    #[default]
    Automatic,
    One,
    Two,
    Four,
}

impl Tiles {
    pub const ALL: [Self; 4] = [Self::Automatic, Self::One, Self::Two, Self::Four];

    pub fn key(self) -> &'static str {
        match self {
            Self::Automatic => "auto",
            Self::One => "1",
            Self::Two => "2",
            Self::Four => "4",
        }
    }

    pub fn from_key(key: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|tiles| tiles.key() == key)
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Automatic => "Automatic (by widget size)",
            Self::One => "One account",
            Self::Two => "Two accounts",
            Self::Four => "Four accounts",
        }
    }

    pub fn count(self, size: Size) -> usize {
        match (self, size) {
            (Self::Automatic, Size::Small) | (Self::One, _) => 1,
            (Self::Automatic, Size::Medium) | (Self::Two, _) => 2,
            (Self::Automatic, Size::Large) | (Self::Four, _) => 4,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Size {
    Small,
    #[default]
    Medium,
    Large,
}

/// A widget's settings, kept by the widget host as the widget's custom state.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Config {
    pub focus: Focus,
    pub tiles: Tiles,
}

impl Config {
    /// The custom state text. JSON, so later versions can add settings.
    pub fn to_state(&self) -> String {
        json!({ "focus": self.focus.key(), "tiles": self.tiles.key() }).to_string()
    }

    /// Settings from custom state; anything missing or unreadable takes its default.
    pub fn from_state(state: &str) -> Self {
        let value: Value = serde_json::from_str(state).unwrap_or(Value::Null);
        Self {
            focus: value
                .get("focus")
                .and_then(Value::as_str)
                .and_then(Focus::from_key)
                .unwrap_or_default(),
            tiles: value
                .get("tiles")
                .and_then(Value::as_str)
                .and_then(Tiles::from_key)
                .unwrap_or_default(),
        }
    }
}

/// The action verbs cards send back.
pub mod verbs {
    pub const OPEN: &str = "open";
    pub const SAVE: &str = "save";
    pub const CANCEL: &str = "cancel";
}

fn card(body: Vec<Value>, actions: Vec<Value>) -> Value {
    let mut card = json!({
        "type": "AdaptiveCard",
        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
        "version": "1.5",
        "body": body,
    });
    if !actions.is_empty() {
        card["actions"] = Value::Array(actions);
    }
    card
}

fn text(text: impl Into<String>) -> Value {
    json!({ "type": "TextBlock", "text": text.into(), "wrap": true })
}

fn subtle(value: impl Into<String>) -> Value {
    let mut block = text(value);
    block["isSubtle"] = json!(true);
    block["size"] = json!("Small");
    block
}

fn open_action() -> Value {
    json!({ "type": "Action.Execute", "title": "Open CodexBar", "verb": verbs::OPEN })
}

/// A message card: a title line, an explanation and an Open action.
fn message(title: &str, body: &str) -> Value {
    let mut heading = text(title);
    heading["weight"] = json!("Bolder");
    card(vec![heading, subtle(body)], vec![open_action()])
}

/// The focus's display name, if it still exists in the snapshot.
fn focus_name(focus: &Focus, snapshot: &WidgetSnapshot) -> Option<String> {
    match focus {
        Focus::All => Some("All accounts".into()),
        Focus::Provider(key) => snapshot
            .accounts
            .iter()
            .find(|account| &account.provider == key)
            .map(|account| account.provider_name.clone()),
        Focus::Group(id) => snapshot
            .groups
            .iter()
            .find(|group| &group.id == id)
            .map(|group| group.name.clone()),
    }
}

fn focused<'a>(focus: &Focus, snapshot: &'a WidgetSnapshot) -> Vec<&'a WidgetAccount> {
    snapshot
        .accounts
        .iter()
        .filter(|account| match focus {
            Focus::All => true,
            Focus::Provider(key) => &account.provider == key,
            Focus::Group(id) => account.group.as_ref() == Some(id),
        })
        .collect()
}

/// A usage bar: two weighted columns, used and left. The color follows the account's status.
fn bar(percent: f64, status: Option<&str>) -> Value {
    let style = match status {
        Some("At risk" | "Limit soon") => "attention",
        Some(_) => "warning",
        None => "good",
    };
    let used = percent.round().clamp(0.0, 100.0) as u32;
    let mut columns = Vec::new();
    if used > 0 {
        columns.push(
            json!({ "type": "Column", "width": used.to_string(), "style": style, "minHeight": "6px", "items": [] }),
        );
    }
    if used < 100 {
        columns.push(json!({ "type": "Column", "width": (100 - used).to_string(), "style": "emphasis", "minHeight": "6px", "items": [] }));
    }
    json!({ "type": "ColumnSet", "spacing": "Small", "columns": columns })
}

/// One account: its name, the primary metric's value and bar, and how current it is.
fn tile(account: &WidgetAccount, now: DateTime<Utc>, compact: bool) -> Value {
    let mut items = Vec::new();
    let mut name = text(account.name.clone());
    name["weight"] = json!("Bolder");
    name["size"] = json!("Small");
    name["wrap"] = json!(false);
    items.push(name);
    match (account.health, account.metrics.first()) {
        (WidgetHealth::Unavailable, _) => items.push(subtle("Unavailable")),
        (_, None) => items.push(subtle("No limits to show")),
        (health, Some(metric)) => {
            let value = if compact {
                metric.value.clone()
            } else {
                format!("{}: {}", metric.label, metric.value)
            };
            let mut line = text(value);
            line["size"] = json!("Small");
            line["spacing"] = json!("None");
            items.push(line);
            if let Some(percent) = metric.used_percent {
                items.push(bar(percent, account.status.as_deref()));
            }
            let status = match (health, &account.status, account.updated_at) {
                (WidgetHealth::Stale, _, Some(at)) => Some(format!("Last known, {}", age_label(at, now))),
                (WidgetHealth::Stale, _, None) => Some("Last known".into()),
                (_, Some(status), _) => Some(status.clone()),
                _ => None,
            };
            if let Some(status) = status.filter(|_| !compact || health == WidgetHealth::Stale) {
                items.push(subtle(status));
            }
        }
    }
    json!({ "type": "Container", "items": items })
}

/// The widget's card for the current snapshot (or why there is none).
pub fn render(snapshot: &Result<WidgetSnapshot, LoadError>, config: &Config, size: Size, now: DateTime<Utc>) -> Value {
    let snapshot = match snapshot {
        Ok(snapshot) => snapshot,
        Err(LoadError::Missing) => {
            return message("CodexBar", "Open CodexBar to see your AI coding usage here.");
        }
        Err(LoadError::Newer) => {
            return message(
                "Update CodexBar",
                "This widget is older than CodexBar's usage data. Install the latest CodexBar package.",
            );
        }
        Err(LoadError::Invalid) => {
            return message(
                "CodexBar",
                "CodexBar's usage data couldn't be read. Open CodexBar to refresh it.",
            );
        }
    };
    let Some(title) = focus_name(&config.focus, snapshot) else {
        return message(
            "Nothing to show",
            "The provider or group this widget showed is gone. Choose another in Customize widget.",
        );
    };
    let accounts = focused(&config.focus, snapshot);
    if accounts.is_empty() {
        return message(
            &title,
            if snapshot.accounts.is_empty() {
                "No accounts yet. Add one in CodexBar's Settings."
            } else {
                "No accounts here yet."
            },
        );
    }

    let count = config.tiles.count(size).min(accounts.len());
    let compact = size == Size::Small && count > 1;
    let tiles: Vec<Value> = accounts
        .iter()
        .take(count)
        .map(|account| tile(account, now, compact))
        .collect();
    let mut body = Vec::new();
    let mut heading = text(title);
    heading["weight"] = json!("Bolder");
    heading["size"] = json!("Small");
    heading["isSubtle"] = json!(true);
    body.push(heading);
    // Four tiles sit two by two; fewer stack, unless a wide layout has room for two side by side.
    if count == 4 || (count == 2 && size == Size::Large) {
        for pair in tiles.chunks(2) {
            let columns: Vec<Value> = pair
                .iter()
                .map(|tile| json!({ "type": "Column", "width": "stretch", "items": [tile] }))
                .collect();
            body.push(json!({ "type": "ColumnSet", "columns": columns }));
        }
    } else {
        body.extend(tiles);
    }
    let age = now - snapshot.generated_at;
    let footer = if age > NOT_RUNNING_AFTER {
        format!(
            "CodexBar isn't running. Updated {}.",
            age_label(snapshot.generated_at, now)
        )
    } else {
        format!("Updated {}", age_label(snapshot.generated_at, now))
    };
    body.push(subtle(footer));
    let actions = if age > NOT_RUNNING_AFTER {
        vec![open_action()]
    } else {
        Vec::new()
    };
    let mut card = card(body, actions);
    // Tapping the widget opens CodexBar.
    card["selectAction"] = json!({ "type": "Action.Execute", "verb": verbs::OPEN });
    card
}

/// The Customize widget card: which accounts, and how many.
pub fn customize(snapshot: &Result<WidgetSnapshot, LoadError>, config: &Config) -> Value {
    let mut choices = vec![json!({ "title": "All accounts", "value": Focus::All.key() })];
    if let Ok(snapshot) = snapshot {
        let mut providers: Vec<(&str, &str)> = Vec::new();
        for account in &snapshot.accounts {
            if !providers.iter().any(|(key, _)| *key == account.provider) {
                providers.push((&account.provider, &account.provider_name));
            }
        }
        for (key, name) in providers {
            choices.push(json!({ "title": name, "value": Focus::Provider(key.to_owned()).key() }));
        }
        for group in &snapshot.groups {
            choices.push(
                json!({ "title": format!("Group: {}", group.name), "value": Focus::Group(group.id.clone()).key() }),
            );
        }
    }
    let tiles: Vec<Value> = Tiles::ALL
        .iter()
        .map(|tiles| json!({ "title": tiles.label(), "value": tiles.key() }))
        .collect();
    let mut heading = text("Customize CodexBar");
    heading["weight"] = json!("Bolder");
    card(
        vec![
            heading,
            json!({ "type": "Input.ChoiceSet", "id": "focus", "label": "Show", "style": "compact",
                    "value": config.focus.key(), "choices": choices }),
            json!({ "type": "Input.ChoiceSet", "id": "tiles", "label": "Layout", "style": "compact",
                    "value": config.tiles.key(), "choices": tiles }),
        ],
        vec![
            json!({ "type": "Action.Execute", "title": "Save", "verb": verbs::SAVE }),
            json!({ "type": "Action.Execute", "title": "Cancel", "verb": verbs::CANCEL }),
        ],
    )
}

/// The settings a Save action carries (`{"focus": ..., "tiles": ...}`), applied over `config`.
pub fn saved(config: &Config, data: &str) -> Config {
    let value: Value = serde_json::from_str(data).unwrap_or(Value::Null);
    Config {
        focus: value
            .get("focus")
            .and_then(Value::as_str)
            .and_then(Focus::from_key)
            .unwrap_or_else(|| config.focus.clone()),
        tiles: value
            .get("tiles")
            .and_then(Value::as_str)
            .and_then(Tiles::from_key)
            .unwrap_or(config.tiles),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use codexbar_store::widgets::{WidgetGroup, WidgetMetric};

    fn now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-10-10T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc)
    }

    fn account(id: &str, provider: &str, group: Option<&str>, percent: f64) -> WidgetAccount {
        WidgetAccount {
            id: id.into(),
            provider: provider.into(),
            provider_name: provider.to_uppercase(),
            name: format!("{provider} {id}"),
            group: group.map(str::to_owned),
            health: WidgetHealth::Fresh,
            updated_at: Some(now()),
            status: None,
            metrics: vec![WidgetMetric {
                key: "weekly".into(),
                label: "Weekly".into(),
                value: format!("{percent:.0}% used"),
                used_percent: Some(percent),
                resets_at: None,
            }],
        }
    }

    fn snapshot() -> Result<WidgetSnapshot, LoadError> {
        Ok(WidgetSnapshot::new(
            now() - chrono::Duration::minutes(2),
            vec![WidgetGroup {
                id: "g1".into(),
                name: "Work".into(),
            }],
            vec![
                account("a", "claude", Some("g1"), 82.0),
                account("b", "codex", None, 10.0),
                account("c", "claude", None, 0.0),
                account("d", "copilot", Some("g1"), 100.0),
                account("e", "cursor", None, 50.0),
            ],
        ))
    }

    fn all_text(card: &Value) -> String {
        let mut out = String::new();
        fn walk(value: &Value, out: &mut String) {
            match value {
                Value::Object(map) => {
                    if let Some(Value::String(text)) = map.get("text") {
                        out.push_str(text);
                        out.push('\n');
                    }
                    map.values().for_each(|v| walk(v, out));
                }
                Value::Array(items) => items.iter().for_each(|v| walk(v, out)),
                _ => {}
            }
        }
        walk(card, &mut out);
        out
    }

    fn names(card: &Value) -> Vec<String> {
        all_text(card)
            .lines()
            .filter(|line| {
                line.contains(' ')
                    && ["claude", "codex", "copilot", "cursor"]
                        .iter()
                        .any(|p| line.starts_with(p))
            })
            .map(str::to_owned)
            .collect()
    }

    #[test]
    fn automatic_tiles_follow_the_widget_size() {
        let config = Config::default();
        for (size, expected) in [(Size::Small, 1), (Size::Medium, 2), (Size::Large, 4)] {
            let card = render(&snapshot(), &config, size, now());
            assert_eq!(names(&card).len(), expected, "{size:?}");
        }
        assert_eq!(
            names(&render(&snapshot(), &config, Size::Large, now()))[0],
            "claude a",
            "dashboard order"
        );
    }

    #[test]
    fn explicit_tile_counts_win_over_size() {
        for (tiles, expected) in [(Tiles::One, 1), (Tiles::Two, 2), (Tiles::Four, 4)] {
            let config = Config {
                focus: Focus::All,
                tiles,
            };
            assert_eq!(
                names(&render(&snapshot(), &config, Size::Medium, now())).len(),
                expected,
                "{tiles:?}"
            );
        }
    }

    #[test]
    fn provider_and_group_focus_filter_in_dashboard_order() {
        let provider = Config {
            focus: Focus::Provider("claude".into()),
            tiles: Tiles::Four,
        };
        let card = render(&snapshot(), &provider, Size::Large, now());
        assert_eq!(names(&card), ["claude a", "claude c"]);
        assert!(
            all_text(&card).starts_with("CLAUDE\n"),
            "the provider's name heads the card"
        );
        let group = Config {
            focus: Focus::Group("g1".into()),
            tiles: Tiles::Four,
        };
        let card = render(&snapshot(), &group, Size::Large, now());
        assert_eq!(names(&card), ["claude a", "copilot d"]);
        assert!(all_text(&card).starts_with("Work\n"));
    }

    #[test]
    fn a_removed_group_or_provider_degrades_to_a_message() {
        let gone = Config {
            focus: Focus::Group("deleted".into()),
            tiles: Tiles::Automatic,
        };
        let text = all_text(&render(&snapshot(), &gone, Size::Medium, now()));
        assert!(text.contains("is gone") && text.contains("Customize widget"), "{text}");
    }

    #[test]
    fn loading_newer_and_broken_snapshots_say_so() {
        let config = Config::default();
        assert!(all_text(&render(&Err(LoadError::Missing), &config, Size::Medium, now())).contains("Open CodexBar"));
        assert!(all_text(&render(&Err(LoadError::Newer), &config, Size::Medium, now())).contains("Update CodexBar"));
        assert!(all_text(&render(&Err(LoadError::Invalid), &config, Size::Medium, now())).contains("couldn't be read"));
        let empty = Ok(WidgetSnapshot::new(now(), vec![], vec![]));
        assert!(all_text(&render(&empty, &config, Size::Medium, now())).contains("No accounts yet"));
    }

    #[test]
    fn stale_and_unavailable_accounts_and_a_stopped_app_are_marked() {
        let mut snapshot = snapshot().unwrap();
        snapshot.accounts[0].health = WidgetHealth::Stale;
        snapshot.accounts[0].updated_at = Some(now() - chrono::Duration::minutes(30));
        snapshot.accounts[1].health = WidgetHealth::Unavailable;
        let card = render(&Ok(snapshot.clone()), &Config::default(), Size::Medium, now());
        let text = all_text(&card);
        assert!(text.contains("Last known, 30m ago"), "{text}");
        assert!(text.contains("Unavailable"));
        assert!(text.contains("Updated 2m ago"));
        assert!(card.get("actions").is_none(), "a running app needs no Open button");

        snapshot.generated_at = now() - chrono::Duration::hours(3);
        let card = render(&Ok(snapshot), &Config::default(), Size::Medium, now());
        assert!(all_text(&card).contains("CodexBar isn't running"));
        assert_eq!(card["actions"][0]["verb"], verbs::OPEN);
    }

    #[test]
    fn bars_split_used_and_left_and_color_by_status() {
        let full = bar(100.0, Some("Limit soon"));
        assert_eq!(full["columns"].as_array().unwrap().len(), 1);
        assert_eq!(full["columns"][0]["style"], "attention");
        let empty = bar(0.0, None);
        assert_eq!(empty["columns"].as_array().unwrap().len(), 1);
        assert_eq!(empty["columns"][0]["style"], "emphasis");
        let watch = bar(42.4, Some("Watch"));
        assert_eq!(watch["columns"][0]["width"], "42");
        assert_eq!(watch["columns"][0]["style"], "warning");
        assert_eq!(watch["columns"][1]["width"], "58");
    }

    #[test]
    fn four_tiles_sit_two_by_two() {
        let config = Config {
            focus: Focus::All,
            tiles: Tiles::Four,
        };
        let card = render(&snapshot(), &config, Size::Large, now());
        let rows: Vec<&Value> = card["body"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|item| item["type"] == "ColumnSet")
            .collect();
        assert_eq!(rows.len(), 2);
        assert!(rows.iter().all(|row| row["columns"].as_array().unwrap().len() == 2));
    }

    #[test]
    fn config_round_trips_through_custom_state_and_tolerates_junk() {
        let config = Config {
            focus: Focus::Group("g1".into()),
            tiles: Tiles::Two,
        };
        assert_eq!(Config::from_state(&config.to_state()), config);
        assert_eq!(Config::from_state(""), Config::default());
        assert_eq!(
            Config::from_state(r#"{"focus":"provider:","tiles":"9"}"#),
            Config::default()
        );
    }

    #[test]
    fn customize_offers_providers_once_and_groups_and_save_applies_it() {
        let card = customize(&snapshot(), &Config::default());
        let choices: Vec<&str> = card["body"][1]["choices"]
            .as_array()
            .unwrap()
            .iter()
            .map(|choice| choice["value"].as_str().unwrap())
            .collect();
        assert_eq!(
            choices,
            [
                "all",
                "provider:claude",
                "provider:codex",
                "provider:copilot",
                "provider:cursor",
                "group:g1"
            ]
        );
        assert_eq!(card["body"][2]["choices"].as_array().unwrap().len(), 4);
        let saved = saved(&Config::default(), r#"{"focus":"group:g1","tiles":"4"}"#);
        assert_eq!(
            saved,
            Config {
                focus: Focus::Group("g1".into()),
                tiles: Tiles::Four
            }
        );
        assert_eq!(
            super::saved(&saved, "{}"),
            saved,
            "missing inputs keep the current settings"
        );
    }

    #[test]
    fn cards_are_adaptive_cards() {
        for card in [
            render(&snapshot(), &Config::default(), Size::Large, now()),
            render(&Err(LoadError::Missing), &Config::default(), Size::Small, now()),
            customize(&snapshot(), &Config::default()),
        ] {
            assert_eq!(card["type"], "AdaptiveCard");
            assert_eq!(card["version"], "1.5");
            assert!(card["body"].as_array().is_some_and(|body| !body.is_empty()));
        }
    }
}

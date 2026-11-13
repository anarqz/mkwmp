//! The launcher window, in the project site's 16-bit racing look.
//!
//! Everything is laid out on a fixed 480 × 800 design canvas and the whole
//! canvas is scaled to fit the window (uniform zoom, centred), like a game
//! screen: resizing never re-flows or stretches the layout. Steam Deck Game
//! Mode opens it fullscreen, which simply scales it up.

use std::sync::Arc;
use std::time::Duration;

use eframe::egui::{
    self, epaint, pos2, vec2, Align2, Color32, CornerRadius, FontData, FontDefinitions, FontFamily, FontId, Rect,
    Response, RichText, Sense, Shadow, Stroke, StrokeKind, TextEdit, Vec2,
};

use crate::config::{valid_name, ClientConfig};
use crate::core::{Core, RegisterView, ServerView, SessionView};
use crate::seat::Phase;
use crate::update::VERSION;

const INK: Color32 = Color32::from_rgb(0x1b, 0x15, 0x30);
const PAPER: Color32 = Color32::from_rgb(0xff, 0xf8, 0xe6);
const RED: Color32 = Color32::from_rgb(0xe6, 0x00, 0x12);
const RED_D: Color32 = Color32::from_rgb(0x8f, 0x00, 0x0b);
const YELLOW: Color32 = Color32::from_rgb(0xff, 0xd4, 0x00);
const YELLOW_D: Color32 = Color32::from_rgb(0xb3, 0x86, 0x00);
const BLUE: Color32 = Color32::from_rgb(0x0a, 0x6c, 0xff);
const BLUE_D: Color32 = Color32::from_rgb(0x03, 0x40, 0xa3);
const GREEN: Color32 = Color32::from_rgb(0x2f, 0xb5, 0x2f);
const GREEN_D: Color32 = Color32::from_rgb(0x17, 0x70, 0x1b);
const SKY: Color32 = Color32::from_rgb(0x5e, 0xc8, 0xff);
const SKY_2: Color32 = Color32::from_rgb(0xa6, 0xe3, 0xff);
const ASPHALT: Color32 = Color32::from_rgb(0x5a, 0x5a, 0x6e);
const MUTED: Color32 = Color32::from_rgb(0x6b, 0x63, 0x86);
const SKY_TEXT: Color32 = Color32::from_rgb(0xa6, 0xe3, 0xff);
const PINK: Color32 = Color32::from_rgb(0xff, 0x5e, 0x8a);
const PINK_D: Color32 = Color32::from_rgb(0xa8, 0x1f, 0x4a);
pub const REPO_URL: &str = "https://github.com/anarqz/mkwmp";
pub const DONATE_URL: &str = "https://ko-fi.com/anarqz";
const GOOD: Color32 = Color32::from_rgb(0x7d, 0xff, 0x7d);
const BAD: Color32 = Color32::from_rgb(0xff, 0x8a, 0x8a);

/// The design canvas, in points at zoom 1.
const CANVAS: Vec2 = Vec2::new(960.0, 660.0);
/// Hills, flag, road with karts, and the footer along the bottom.
const GROUND: f32 = 106.0;
/// Content width (canvas minus margins); each card is half of it.
const COLUMN: f32 = 912.0;
const GAP: f32 = 24.0;
const CARD_W: f32 = (COLUMN - GAP) / 2.0;
/// Width inside a card (card minus border and margins).
const INNER: f32 = CARD_W - 44.0;
/// Both cards' content height (fixed by design, so they always match).
const CARD_H: f32 = 252.0;

/// Steam Deck Game Mode (gamescope): open fullscreen.
pub fn game_mode() -> bool {
    std::env::var_os("SteamGamepadUI").is_some() || std::env::var_os("GAMESCOPE_WAYLAND_DISPLAY").is_some()
}

pub fn run(core: Arc<Core>) -> Result<(), String> {
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("MKWMP")
        .with_inner_size([CANVAS.x * 0.9, CANVAS.y * 0.9])
        .with_min_inner_size([CANVAS.x * 0.6, CANVAS.y * 0.6]);
    if game_mode() {
        viewport = viewport.with_fullscreen(true);
    }
    let options = eframe::NativeOptions { viewport, ..Default::default() };
    eframe::run_native(
        "MKWMP",
        options,
        Box::new(move |cc| {
            setup_style(&cc.egui_ctx);
            Ok(Box::new(App::new(core)))
        }),
    )
    .map_err(|e| format!("window: {e}"))
}

fn setup_style(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    fonts.font_data.insert(
        "pixel".into(),
        Arc::new(FontData::from_static(include_bytes!("../assets/PressStart2P-Regular.ttf"))),
    );
    fonts.families.insert(FontFamily::Name("pixel".into()), vec!["pixel".into()]);
    ctx.set_fonts(fonts);

    // Light "paper" widgets: white fields with ink outlines, square corners.
    let mut v = egui::Visuals::light();
    v.override_text_color = Some(INK);
    v.extreme_bg_color = Color32::WHITE;
    v.text_cursor.stroke = Stroke::new(2.0, INK);
    v.selection.bg_fill = SKY_2;
    v.selection.stroke = Stroke::new(1.0, INK);
    for w in [
        &mut v.widgets.noninteractive,
        &mut v.widgets.inactive,
        &mut v.widgets.hovered,
        &mut v.widgets.active,
        &mut v.widgets.open,
    ] {
        w.corner_radius = CornerRadius::ZERO;
        w.fg_stroke = Stroke::new(2.0, INK);
    }
    v.widgets.inactive.bg_stroke = Stroke::new(2.0, INK);
    v.widgets.inactive.bg_fill = Color32::WHITE;
    v.widgets.inactive.weak_bg_fill = Color32::WHITE;
    v.widgets.hovered.bg_stroke = Stroke::new(2.0, BLUE);
    v.widgets.hovered.weak_bg_fill = Color32::WHITE;
    v.widgets.active.bg_stroke = Stroke::new(2.0, BLUE);
    v.widgets.noninteractive.bg_stroke = Stroke::NONE;
    ctx.set_visuals(v);
    ctx.all_styles_mut(|s| {
        s.spacing.item_spacing = vec2(8.0, 8.0);
        s.spacing.interact_size.y = 30.0;
        s.spacing.button_padding = vec2(10.0, 6.0);
        for f in s.text_styles.values_mut() {
            f.size += 1.0;
        }
    });
}

fn pixel(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name("pixel".into()))
}

struct App {
    core: Arc<Core>,
    draft: ClientConfig,
    friends: [String; 8],
    pin_entry: String,
    error: Option<String>,
    confirm_delete: bool,
    /// DELETE was confirmed; waiting for the server.
    deleting: bool,
}

impl App {
    fn new(core: Arc<Core>) -> Self {
        let draft = core.ui().cfg;
        let mut friends: [String; 8] = Default::default();
        for (slot, f) in friends.iter_mut().zip(draft.friends.iter()) {
            *slot = f.clone();
        }
        Self { core, draft, friends, pin_entry: String::new(), error: None, confirm_delete: false, deleting: false }
    }

    fn save(&mut self) {
        self.draft.friends = self.friends.iter().map(|f| f.trim().to_string()).filter(|f| !f.is_empty()).collect();
        if self.draft != self.core.ui().cfg {
            self.error = self.core.set_config(self.draft.clone()).err();
        }
    }
}

/// Scale the whole canvas to the window: zoom = how many canvases fit.
fn fit_zoom(ctx: &egui::Context) {
    let native = ctx.native_pixels_per_point().unwrap_or(1.0);
    let window = ctx.content_rect().size() * ctx.pixels_per_point() / native; // in zoom-1 points
    let zoom = (window.x / CANVAS.x).min(window.y / CANVAS.y).clamp(0.5, 4.0);
    if (zoom - ctx.zoom_factor()).abs() > 0.01 {
        ctx.set_zoom_factor(zoom);
    }
}

impl eframe::App for App {
    fn ui(&mut self, root: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = root.ctx().clone();
        fit_zoom(&ctx);
        // ~30 fps for the sky and the karts while focused; nearly still in
        // the background (the game is usually in front).
        let focused = ctx.input(|i| i.viewport().focused.unwrap_or(true));
        ctx.request_repaint_after(Duration::from_millis(if focused { 33 } else { 500 }));
        let t = ctx.input(|i| i.time) as f32;
        let st = self.core.ui();
        // The PIN can arrive from a background REGISTER.
        // The PIN can change in the background (REGISTER, delete).
        if st.cfg.name.eq_ignore_ascii_case(&self.draft.name) && st.cfg.pin != self.draft.pin && !st.cfg.pin.is_empty() {
            self.draft.pin = st.cfg.pin.clone();
        }
        if self.deleting && st.register != RegisterView::Working {
            self.deleting = false;
            if st.cfg.pin.is_empty() {
                self.draft.pin.clear(); // deleted: REGISTER is back
            }
        }
        let busy = !matches!(st.session, SessionView::Idle | SessionView::Failed(_));

        let screen = ctx.content_rect();
        paint_backdrop(root.painter(), screen, t);

        // The canvas, centred.
        let canvas = Rect::from_center_size(screen.center(), CANVAS);
        let col = Rect::from_min_size(
            pos2(canvas.center().x - COLUMN / 2.0, canvas.top() + 16.0),
            vec2(COLUMN, CANVAS.y - 16.0 - GROUND),
        );
        root.scope_builder(egui::UiBuilder::new().max_rect(col), |ui| {
            ui.spacing_mut().item_spacing.y = 12.0;
            ui.spacing_mut().item_spacing.y = 10.0;
            title(ui);
            server_pill(ui, &st.server);
            ui.add_space(4.0);
            ui.horizontal_top(|ui| {
                ui.spacing_mut().item_spacing.x = GAP;
                let a = self.player_card(ui, &st, busy);
                let b = self.friends_card(ui, &st, busy);
                if std::env::var_os("MKWMP_GUI_DEBUG").is_some() {
                    eprintln!("card heights: player {a:.1} friends {b:.1}");
                }
            });
            ui.add_space(6.0);
            self.play_row(ui, busy, st.stars);
            status_pill(ui, &st, &self.draft, &self.error);
        });

        if self.confirm_delete {
            self.delete_dialog(&ctx);
        }

        // Footer on the road.
        let foot = format!("v{VERSION}  ·  {}", if st.update.is_empty() { "…" } else { &st.update });
        root.painter().text(
            pos2(screen.center().x, screen.bottom() - 10.0),
            Align2::CENTER_CENTER,
            foot,
            pixel(8.0),
            PAPER,
        );
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.save();
        self.core.stop();
    }
}

impl App {
    fn player_card(&mut self, ui: &mut egui::Ui, st: &crate::core::Ui, busy: bool) -> f32 {
        let min_h = CARD_H;
        card(ui, "PLAYER", BLUE, BLUE_D, min_h, |ui| {
            // Name + PIN (or REGISTER).
            field_label(ui, "NAME");
            ui.horizontal(|ui| {
                let has_pin = self.draft.pin.len() == 6;
                let r = ui.add_enabled(
                    !busy,
                    TextEdit::singleline(&mut self.draft.name)
                        .hint_text("your name")
                        .char_limit(16)
                        .margin(vec2(8.0, 6.0))
                        .desired_width(INNER - 214.0), // room for PIN badge + X
                );
                if r.changed() {
                    self.draft.pin.clear(); // a PIN belongs to one name
                    self.core.reset_register();
                }
                if r.lost_focus() {
                    self.save();
                }
                let taken = st.register == RegisterView::Taken && !has_pin;
                if has_pin {
                    pin_badge(ui, &self.draft.pin);
                    let ok = !busy && st.register != RegisterView::Working;
                    if pixel_button(ui, "X", vec2(32.0, 32.0), RED, RED_D, PAPER, 11.0, ok)
                        .on_hover_text("Delete this profile from the server")
                        .clicked()
                    {
                        self.confirm_delete = true;
                    }
                } else if taken {
                    // The name is someone's: if it's yours, its PIN goes here.
                    let r = ui.add(
                        TextEdit::singleline(&mut self.pin_entry)
                            .char_limit(6)
                            .hint_text("PIN")
                            .margin(vec2(8.0, 6.0))
                            .desired_width(58.0),
                    );
                    if r.changed() {
                        self.pin_entry.retain(|c| c.is_ascii_digit());
                    }
                    let ok = self.pin_entry.len() == 6;
                    if pixel_button(ui, "USE", vec2(56.0, 32.0), BLUE, BLUE_D, PAPER, 10.0, ok).clicked() {
                        self.draft.pin = std::mem::take(&mut self.pin_entry);
                        self.save();
                        self.core.reset_register();
                    }
                } else {
                    let working = st.register == RegisterView::Working;
                    let ok = !busy && !working && valid_name(&self.draft.name);
                    let label = if working { "…" } else { "REGISTER" };
                    if pixel_button(ui, label, vec2(136.0, 32.0), YELLOW, YELLOW_D, INK, 10.0, ok).clicked() {
                        self.save();
                        self.core.register();
                    }
                }
            });
            // Exactly one message line, whatever the state (fixed card height).
            let (msg, color) = if !self.draft.name.is_empty() && !valid_name(&self.draft.name) {
                ("3-16 letters, digits, - or _".to_string(), RED_D)
            } else if let RegisterView::Failed(m) = &st.register {
                (m.clone(), RED_D)
            } else if st.register == RegisterView::Taken && self.draft.pin.len() != 6 {
                ("That name is registered. If it's yours, enter its PIN.".to_string(), RED_D)
            } else if self.draft.pin.len() == 6 {
                ("Your PIN: keep it to use this name on another device.".to_string(), MUTED)
            } else {
                ("Register a name to play. You'll get a 6-digit PIN.".to_string(), MUTED)
            };
            hint(ui, &msg, color);

            field_label(ui, "SERVER");
            let r = ui.add_enabled(
                !busy,
                TextEdit::singleline(&mut self.draft.server).margin(vec2(8.0, 6.0)).desired_width(INNER),
            );
            if r.lost_focus() {
                self.save();
            }

            field_label(ui, "GAME FOLDER");
            ui.horizontal(|ui| {
                let mut path = self.draft.game.display().to_string();
                let w = if cfg!(windows) { INNER - 112.0 } else { INNER };
                let r = ui.add_enabled(
                    !busy,
                    TextEdit::singleline(&mut path).hint_text("folder with mkw.exe").margin(vec2(8.0, 6.0)).desired_width(w),
                );
                if r.changed() {
                    self.draft.game = path.into();
                }
                if r.lost_focus() {
                    self.save();
                }
                #[cfg(windows)]
                if pixel_button(ui, "BROWSE", vec2(96.0, 32.0), BLUE, BLUE_D, PAPER, 9.0, !busy).clicked() {
                    if let Some(dir) = rfd::FileDialog::new().set_title("Game folder (with mkw.exe)").pick_folder() {
                        self.draft.game = dir;
                        self.save();
                    }
                }
            });
            ui.add_space(2.0);
            let mut steam = !self.draft.launch;
            if ui
                .add_enabled(!busy, egui::Checkbox::new(&mut steam, "I start the game from Steam (Steam Deck)"))
                .changed()
            {
                self.draft.launch = !steam;
                self.save();
            }
        })
    }

    fn friends_card(&mut self, ui: &mut egui::Ui, st: &crate::core::Ui, busy: bool) -> f32 {
        let min_h = CARD_H;
        card(ui, "FRIENDS", GREEN, GREEN_D, min_h, |ui| {
            hint(ui, "Up to 8. Anyone who adds you back is your friend in the game.", MUTED);
            // Two (light + field) pairs: field width minus its 8 pt margins each side.
            let w = (INNER - 2.0 * 14.0 - 3.0 * 8.0 - 2.0 * 16.0) / 2.0;
            for row in 0..4 {
                ui.horizontal(|ui| {
                    for col in 0..2 {
                        let i = row * 2 + col;
                        let name = self.friends[i].trim().to_ascii_lowercase();
                        let live = st.friends.iter().find(|f| f.name.to_ascii_lowercase() == name);
                        let (color, tip) = match live {
                            Some(f) if f.mutual => (GREEN, "online · friends in game"),
                            Some(f) if f.online => (YELLOW, "online · hasn't added you yet"),
                            _ if busy && !name.is_empty() => (MUTED, "offline"),
                            _ => (Color32::from_rgb(0xe4, 0xdc, 0xc8), ""),
                        };
                        light(ui, color).on_hover_text(tip);
                        let r = ui.add(
                            TextEdit::singleline(&mut self.friends[i])
                                .char_limit(16)
                                .hint_text("friend")
                                .margin(vec2(8.0, 4.0))
                                .desired_width(w),
                        );
                        if r.lost_focus() {
                            self.save();
                        }
                    }
                });
            }
        })
    }

    fn delete_dialog(&mut self, ctx: &egui::Context) {
        let modal = egui::Modal::new(egui::Id::unique("delete-profile"))
            .backdrop_color(Color32::from_black_alpha(120))
            .frame(
                egui::Frame::new()
                    .fill(PAPER)
                    .stroke(Stroke::new(4.0, INK))
                    .shadow(Shadow { offset: [8, 8], blur: 0, spread: 0, color: Color32::from_black_alpha(120) })
                    .inner_margin(20),
            );
        let r = modal.show(ctx, |ui| {
            ui.set_width(360.0);
            ui.label(RichText::new("DELETE YOUR PROFILE FROM SERVER?").font(pixel(11.0)).color(INK));
            ui.add_space(6.0);
            ui.label(
                RichText::new(format!(
                    "\"{}\" and its PIN will be removed. Anyone can then register this name.",
                    self.draft.name
                ))
                .size(13.0)
                .color(MUTED),
            );
            ui.add_space(10.0);
            ui.horizontal(|ui| {
                if pixel_button(ui, "DELETE", vec2(150.0, 36.0), RED, RED_D, PAPER, 11.0, true).clicked() {
                    self.confirm_delete = false;
                    self.deleting = true;
                    self.core.delete_profile();
                }
                ui.add_space(8.0);
                if pixel_button(ui, "CANCEL", vec2(150.0, 36.0), BLUE, BLUE_D, PAPER, 11.0, true).clicked() {
                    self.confirm_delete = false;
                }
            });
        });
        if r.should_close() {
            self.confirm_delete = false;
        }
    }

    /// GITHUB · PLAY · DONATE in one row.
    fn play_row(&mut self, ui: &mut egui::Ui, busy: bool, stars: Option<u64>) {
        let (row, _) = ui.allocate_exact_size(vec2(COLUMN, 72.0), Sense::hover());
        let play = Rect::from_center_size(row.center(), vec2(420.0, 68.0));
        let side = vec2(200.0, 40.0);
        let gh = Rect::from_center_size(pos2((row.left() + play.left()) / 2.0, row.center().y), side);
        let kofi = Rect::from_center_size(pos2((play.right() + row.right()) / 2.0, row.center().y), side);

        let problem = self.draft.problem();
        let enabled = busy || problem.is_none();
        let (label, fill, dark) = if busy { ("STOP", BLUE, BLUE_D) } else { ("PLAY", RED, RED_D) };
        let clicked = pixel_button_at(ui, play, "play", label, fill, dark, PAPER, 26.0, enabled).clicked();
        let enter = enabled && !busy && ui.input(|i| i.key_pressed(egui::Key::Enter)) && !ui.ctx().egui_wants_keyboard_input();
        if clicked || enter {
            if busy {
                self.core.stop();
            } else {
                self.save();
                self.core.play();
            }
        }

        let gh_text = match stars {
            Some(n) => format!("GITHUB · {n}"),
            None => "GITHUB".to_string(),
        };
        if icon_button(ui, gh, &gh_text, &STAR, INK, Color32::from_rgb(0x3a, 0x2f, 0x66), YELLOW).clicked() {
            let _ = open::that(REPO_URL);
        }
        if icon_button(ui, kofi, "DONATE", &HEART, PINK, PINK_D, Color32::WHITE).clicked() {
            let _ = open::that(DONATE_URL);
        }
    }
}

// ---- pieces -----------------------------------------------------------------

const STAR: [&str; 7] = ["...#...", "...#...", "#######", ".#####.", "..###..", ".##.##.", ".#...#."];
const HEART: [&str; 6] = [".##.##.", "#######", "#######", ".#####.", "..###..", "...#..."];

/// A tiny bitmap icon, `cell` points per pixel.
fn paint_icon(p: &egui::Painter, at: egui::Pos2, rows: &[&str], cell: f32, color: Color32) {
    for (y, row) in rows.iter().enumerate() {
        for (x, ch) in row.chars().enumerate() {
            if ch == '#' {
                p.rect_filled(Rect::from_min_size(at + vec2(x as f32 * cell, y as f32 * cell), vec2(cell, cell)), 0.0, color);
            }
        }
    }
}

/// A small pixel button at `rect` with a bitmap icon before its label.
fn icon_button(ui: &mut egui::Ui, rect: Rect, text: &str, icon: &[&str], fill: Color32, dark: Color32, icon_color: Color32) -> Response {
    let resp = ui.interact(rect, ui.scope_id().with(text), Sense::click());
    let pressed = resp.is_pointer_button_down_on();
    let r = if pressed { rect.translate(vec2(3.0, 3.0)) } else { rect };
    let p = ui.painter();
    if !pressed {
        p.rect_filled(rect.translate(vec2(3.0, 3.0)), 0.0, INK);
    }
    let fill = if resp.hovered() { fill.lerp_to_gamma(Color32::WHITE, 0.12) } else { fill };
    p.rect_filled(r, 0.0, fill);
    p.rect_filled(Rect::from_min_max(pos2(r.left(), r.bottom() - 3.0), r.max), 0.0, dark);
    p.rect_stroke(r, 0.0, Stroke::new(3.0, INK), StrokeKind::Inside);
    let font = pixel(9.0);
    let galley = p.layout_no_wrap(text.to_string(), font, PAPER);
    let cell = 2.0;
    let icon_w = 7.0 * cell;
    let total = icon_w + 8.0 + galley.size().x;
    let x0 = r.center().x - total / 2.0;
    paint_icon(p, pos2(x0, r.center().y - icon.len() as f32 * cell / 2.0), icon, cell, icon_color);
    p.galley(pos2(x0 + icon_w + 8.0, r.center().y - galley.size().y / 2.0 + 1.0), galley, PAPER);
    if resp.hovered() {
        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
    }
    resp.on_hover_text(if icon.len() == 7 { REPO_URL } else { DONATE_URL })
}

/// A cream panel with a thick ink border, hard drop shadow and a coin heading.
/// Returns the content height it needed, so side-by-side cards can match.
fn card(
    ui: &mut egui::Ui,
    title: &str,
    coin: Color32,
    coin_d: Color32,
    min_h: f32,
    add: impl FnOnce(&mut egui::Ui),
) -> f32 {
    let mut used = 0.0;
    egui::Frame::new()
        .fill(PAPER)
        .stroke(Stroke::new(4.0, INK))
        .shadow(Shadow { offset: [8, 8], blur: 0, spread: 0, color: Color32::from_black_alpha(110) })
        .inner_margin(egui::Margin { left: 18, right: 18, top: 14, bottom: 16 })
        .show(ui, |ui| {
            // Cards may sit in a row; their own contents stack vertically.
            ui.vertical(|ui| {
                ui.set_width(INNER);
                ui.set_min_height(min_h);
                ui.spacing_mut().item_spacing.y = 6.0;
                ui.horizontal(|ui| {
                    let (r, _) = ui.allocate_exact_size(vec2(22.0, 22.0), Sense::hover());
                    let p = ui.painter();
                    p.rect_filled(r, 0.0, coin);
                    p.rect_filled(Rect::from_min_max(pos2(r.left(), r.bottom() - 4.0), r.max), 0.0, coin_d);
                    p.rect_stroke(r, 0.0, Stroke::new(3.0, INK), StrokeKind::Inside);
                    ui.label(RichText::new(title).font(pixel(13.0)).color(INK));
                });
                ui.add_space(2.0);
                add(ui);
                used = ui.min_rect().height();
            });
        });
    used
}

fn field_label(ui: &mut egui::Ui, text: &str) {
    ui.add_space(2.0);
    ui.label(RichText::new(text).font(pixel(8.0)).color(MUTED));
}

fn hint(ui: &mut egui::Ui, text: &str, color: Color32) {
    ui.label(RichText::new(text).size(12.0).color(color));
}

fn pin_badge(ui: &mut egui::Ui, pin: &str) {
    egui::Frame::new()
        .fill(YELLOW)
        .stroke(Stroke::new(3.0, INK))
        .inner_margin(egui::Margin::symmetric(10, 8))
        .show(ui, |ui| {
            ui.label(RichText::new(format!("PIN {pin}")).font(pixel(10.0)).color(INK));
        })
        .response
        .on_hover_text("Your name's PIN. It's also in client.toml.");
}

fn light(ui: &mut egui::Ui, color: Color32) -> Response {
    let (r, resp) = ui.allocate_exact_size(vec2(14.0, 30.0), Sense::hover());
    let sq = Rect::from_center_size(r.center(), vec2(10.0, 10.0));
    ui.painter().rect_filled(sq, 0.0, color);
    ui.painter().rect_stroke(sq, 0.0, Stroke::new(2.0, INK), StrokeKind::Outside);
    resp
}

/// The site's pixel button: bevelled fill, ink border, hard shadow; it
/// sinks into its shadow while pressed. Laid out in the flow.
#[allow(clippy::too_many_arguments)]
fn pixel_button(
    ui: &mut egui::Ui,
    text: &str,
    size: Vec2,
    fill: Color32,
    dark: Color32,
    text_color: Color32,
    font: f32,
    enabled: bool,
) -> Response {
    let (outer, _) = ui.allocate_exact_size(size + vec2(4.0, 4.0), Sense::hover());
    pixel_button_at(ui, Rect::from_min_size(outer.min, size), text, text, fill, dark, text_color, font, enabled)
}

/// The pixel button at a fixed `rect` (its shadow falls 4 points outside).
#[allow(clippy::too_many_arguments)]
fn pixel_button_at(
    ui: &mut egui::Ui,
    rect: Rect,
    id: &str,
    text: &str,
    fill: Color32,
    dark: Color32,
    text_color: Color32,
    font: f32,
    enabled: bool,
) -> Response {
    let sense = if enabled { Sense::click() } else { Sense::hover() };
    let resp = ui.interact(rect, ui.scope_id().with(("pixel_button", id)), sense);
    let pressed = enabled && resp.is_pointer_button_down_on();
    let r = if pressed { rect.translate(vec2(4.0, 4.0)) } else { rect };
    let p = ui.painter();
    let (fill, dark) = if !enabled {
        (fill.lerp_to_gamma(Color32::GRAY, 0.55), dark.lerp_to_gamma(Color32::GRAY, 0.55))
    } else if resp.hovered() {
        (fill.lerp_to_gamma(Color32::WHITE, 0.1), dark)
    } else {
        (fill, dark)
    };
    if !pressed {
        p.rect_filled(rect.translate(vec2(4.0, 4.0)), 0.0, INK);
    }
    p.rect_filled(r, 0.0, fill);
    let b = 4.0;
    p.rect_filled(Rect::from_min_max(pos2(r.left(), r.bottom() - b), r.max), 0.0, dark);
    p.rect_filled(Rect::from_min_max(pos2(r.right() - b, r.top()), r.max), 0.0, dark);
    let shine = Color32::from_white_alpha(80);
    p.rect_filled(Rect::from_min_max(r.min, pos2(r.right() - b, r.top() + b)), 0.0, shine);
    p.rect_filled(Rect::from_min_max(r.min, pos2(r.left() + b, r.bottom() - b)), 0.0, shine);
    p.rect_stroke(r, 0.0, Stroke::new(3.0, INK), StrokeKind::Inside);
    let tc = if enabled { text_color } else { text_color.gamma_multiply(0.7) };
    p.text(r.center() + vec2(0.0, 1.0), Align2::CENTER_CENTER, text, pixel(font), tc);
    if enabled && resp.hovered() {
        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
    }
    resp
}

/// "MKWMP" in the pixel font, white with an ink outline and drop shadow.
fn title(ui: &mut egui::Ui) {
    let (rect, _) = ui.allocate_exact_size(vec2(COLUMN, 64.0), Sense::hover());
    let font = pixel(46.0);
    let p = ui.painter();
    let parts = [("MKW", Color32::WHITE), ("M", RED), ("P", YELLOW)];
    let widths: Vec<f32> =
        parts.iter().map(|(t, _)| p.layout_no_wrap(t.to_string(), font.clone(), INK).size().x).collect();
    let total: f32 = widths.iter().sum();
    let mut x = rect.center().x - total / 2.0;
    let y = rect.center().y;
    for ((t, c), w) in parts.iter().zip(&widths) {
        let at = pos2(x, y);
        p.text(at + vec2(6.0, 6.0), Align2::LEFT_CENTER, *t, font.clone(), INK);
        for (dx, dy) in [(-3.0, 0.0), (3.0, 0.0), (0.0, -3.0), (0.0, 3.0), (-3.0, -3.0), (3.0, 3.0), (-3.0, 3.0), (3.0, -3.0)] {
            p.text(at + vec2(dx, dy), Align2::LEFT_CENTER, *t, font.clone(), INK);
        }
        p.text(at, Align2::LEFT_CENTER, *t, font.clone(), *c);
        x += w;
    }
}

/// A centred ink bar with pixel text (and an optional status square).
fn pill(ui: &mut egui::Ui, text: &str, color: Color32, dot: Option<Color32>) {
    let font = pixel(9.0);
    let galley = ui.painter().layout_no_wrap(text.to_string(), font, color);
    let dot_w = if dot.is_some() { 18.0 } else { 0.0 };
    let size = vec2((galley.size().x + dot_w + 28.0).min(COLUMN), 34.0);
    let (row, _) = ui.allocate_exact_size(vec2(COLUMN, size.y + 4.0), Sense::hover());
    let r = Rect::from_min_size(pos2(row.center().x - size.x / 2.0, row.top()), size);
    let p = ui.painter();
    p.rect_filled(r.translate(vec2(4.0, 4.0)), 0.0, Color32::from_black_alpha(90));
    p.rect_filled(r, 0.0, INK);
    let mut x = r.left() + 14.0;
    if let Some(d) = dot {
        p.rect_filled(Rect::from_center_size(pos2(x + 5.0, r.center().y), vec2(10.0, 10.0)), 0.0, d);
        x += dot_w;
    }
    p.galley(pos2(x, r.center().y - galley.size().y / 2.0 + 1.0), galley, color);
}

fn server_pill(ui: &mut egui::Ui, s: &ServerView) {
    let (dot, text) = match s {
        ServerView::Checking => (MUTED, "CHECKING THE SERVER…".to_string()),
        ServerView::Online { seats_total, seated, queued, .. } => (
            GREEN,
            format!(
                "ONLINE · {}/{} SEATS FREE{}",
                seats_total - seated,
                seats_total,
                if *queued > 0 { format!(" · {queued} IN QUEUE") } else { String::new() }
            ),
        ),
        ServerView::Offline(_) => (RED, "SERVER OFFLINE".to_string()),
    };
    pill(ui, &text, SKY_TEXT, Some(dot));
}

fn status_pill(ui: &mut egui::Ui, st: &crate::core::Ui, draft: &ClientConfig, error: &Option<String>) {
    let (line, c) = match &st.session {
        SessionView::Idle => match draft.problem() {
            Some(p) => (p.to_uppercase(), YELLOW),
            None => ("READY · PRESS PLAY".into(), PAPER),
        },
        SessionView::Starting => ("CONNECTING…".into(), SKY_TEXT),
        SessionView::Failed(e) => (e.clone(), BAD),
        SessionView::Running { phase, game_running, steam } => match phase {
            Phase::Refused(m) => (m.clone(), BAD),
            Phase::Joining => ("TAKING A SEAT…".into(), SKY_TEXT),
            Phase::Queued(n) => (format!("SERVER FULL · YOU ARE #{n} IN THE QUEUE"), YELLOW),
            Phase::WaitingForGame if *steam && !game_running => ("START THE GAME FROM STEAM".into(), SKY_TEXT),
            Phase::WaitingForGame => ("IN GAME · PICK ONLINE PLAY".into(), SKY_TEXT),
            Phase::Seated if *steam && !game_running => ("SEATED · START THE GAME FROM STEAM".into(), GOOD),
            Phase::Seated => ("SEATED · HAVE FUN!".into(), GOOD),
        },
    };
    let (line, c) = match error {
        Some(e) => (e.clone(), BAD),
        None => (line, c),
    };
    pill(ui, &line, c, None);
}

/// A generic go-kart, facing right. h = helmet, v = visor, b = body,
/// d = body shade, w = wheel. Not any particular character.
const KART: [&str; 11] = [
    "......hhhh......",
    ".....hhhhhh.....",
    ".....hvvvvh.....",
    "......hhhh......",
    "...bbbbbbbbbb...",
    "..bbbbbbbbbbbbb.",
    ".bbbbbbbbbbbbbbb",
    ".ddddddddddddddd",
    "..www......www..",
    ".wwwww....wwwww.",
    "..www......www..",
];

fn paint_kart(p: &egui::Painter, at: egui::Pos2, cell: f32, body: Color32, dark: Color32) {
    let helmet = body.lerp_to_gamma(Color32::WHITE, 0.45);
    for (y, row) in KART.iter().enumerate() {
        for (x, ch) in row.chars().enumerate() {
            let c = match ch {
                'h' => helmet,
                'v' => INK,
                'b' => body,
                'd' => dark,
                'w' => INK,
                _ => continue,
            };
            p.rect_filled(Rect::from_min_size(at + vec2(x as f32 * cell, y as f32 * cell), vec2(cell, cell)), 0.0, c);
        }
    }
}

/// Sky gradient with drifting clouds, slow pixel hills, the checkered flag,
/// and karts racing along the road at the bottom. `t` = seconds.
fn paint_backdrop(p: &egui::Painter, screen: Rect, t: f32) {
    let mut mesh = epaint::Mesh::default();
    mesh.colored_vertex(screen.left_top(), SKY);
    mesh.colored_vertex(screen.right_top(), SKY);
    mesh.colored_vertex(screen.left_bottom(), SKY_2);
    mesh.colored_vertex(screen.right_bottom(), SKY_2);
    mesh.add_triangle(0, 1, 2);
    mesh.add_triangle(1, 2, 3);
    p.add(mesh);

    // A sky full of blocky clouds drifting right, wrapping around: big slow
    // ones far back (fainter), small quicker ones in front.
    let span = screen.width() + 160.0;
    let sky_h = (screen.height() - 120.0).max(100.0);
    let cloud = |x0: f32, y0: f32, s: f32, speed: f32, alpha: u8| {
        let cx = screen.left() - 80.0 + (x0 * span + t * speed).rem_euclid(span);
        let cy = screen.top() + 20.0 + y0 * sky_h;
        let c = Color32::from_white_alpha(alpha);
        for (dx, dy, w, h) in [(-3.0, 0.0, 6.0, 2.0), (-2.0, -1.0, 4.0, 1.0), (-1.0, -2.0, 2.0, 1.0), (1.0, -1.0, 1.5, 1.0)] {
            p.rect_filled(Rect::from_min_size(pos2(cx + dx * s, cy + dy * s), vec2(w * s, h * s)), 0.0, c);
        }
    };
    // (x phase, height 0..1, size, speed px/s, alpha)
    const CLOUDS: [(f32, f32, f32, f32, u8); 16] = [
        (0.02, 0.05, 11.0, 3.0, 150),
        (0.31, 0.12, 12.0, 2.5, 140),
        (0.58, 0.02, 10.0, 3.5, 150),
        (0.83, 0.20, 13.0, 2.0, 135),
        (0.15, 0.38, 10.0, 3.0, 140),
        (0.70, 0.45, 11.0, 2.8, 140),
        (0.44, 0.62, 12.0, 2.2, 130),
        (0.93, 0.70, 10.0, 3.2, 140),
        (0.08, 0.18, 7.0, 7.0, 235),
        (0.25, 0.52, 6.0, 9.0, 235),
        (0.40, 0.08, 6.0, 8.0, 240),
        (0.52, 0.33, 7.0, 6.5, 235),
        (0.66, 0.15, 5.0, 10.0, 240),
        (0.78, 0.58, 6.0, 8.5, 235),
        (0.88, 0.30, 7.0, 7.5, 240),
        (0.97, 0.48, 5.0, 11.0, 240),
    ];
    for (x0, y0, size, speed, alpha) in CLOUDS {
        cloud(x0, y0, size, speed, alpha);
    }

    let road = Rect::from_min_max(pos2(screen.left(), screen.bottom() - 62.0), screen.max);
    let flag_top = road.top() - 12.0;

    // Rolling pixel hills, creeping left.
    let step = 6.0;
    let shift = (t * 5.0).rem_euclid(2000.0);
    let mut x = screen.left();
    while x < screen.right() {
        let u = (x + shift) / 70.0;
        let h = 20.0 + 16.0 * (u.sin() * 0.6 + (u * 0.37).sin() * 0.4).abs();
        let h = (h / step).round() * step;
        p.rect_filled(Rect::from_min_max(pos2(x, flag_top - h), pos2(x + step, flag_top)), 0.0, GREEN);
        p.rect_filled(Rect::from_min_max(pos2(x, flag_top - h), pos2(x + step, flag_top - h + 3.0)), 0.0, GREEN_D);
        x += step;
    }

    // Checkered flag strip.
    let strip = Rect::from_min_max(pos2(screen.left(), flag_top), pos2(screen.right(), road.top()));
    let n = (strip.width() / 12.0).ceil() as i32;
    for i in 0..n {
        for j in 0..2 {
            let c = if (i + j) % 2 == 0 { PAPER } else { INK };
            p.rect_filled(
                Rect::from_min_size(strip.min + vec2(i as f32 * 12.0, j as f32 * 6.0), vec2(12.0, 6.0)),
                0.0,
                c,
            );
        }
    }

    // Road with lane dashes scrolling the other way (a sense of speed).
    p.rect_filled(road, 0.0, ASPHALT);
    let dash_shift = (t * 30.0).rem_euclid(40.0);
    let mut x = road.left() - dash_shift;
    while x < road.right() {
        p.rect_filled(
            Rect::from_min_size(pos2(x, road.top() + 38.0), vec2(20.0, 3.0)),
            0.0,
            Color32::from_white_alpha(110),
        );
        x += 40.0;
    }

    // Karts: different speeds, so they overtake each other; a 1-cell bounce.
    let cell = 2.0;
    let kw = 16.0 * cell;
    let lap = screen.width() + kw * 2.0;
    let karts = [
        (RED, RED_D, 0.00, 26.0),
        (BLUE, BLUE_D, 0.21, 31.0),
        (GREEN, GREEN_D, 0.43, 23.0),
        (YELLOW, YELLOW_D, 0.62, 29.0),
        (PINK, PINK_D, 0.81, 34.0),
    ];
    for (i, (body, dark, phase, speed)) in karts.iter().enumerate() {
        let x = screen.left() - kw + (phase * lap + t * speed).rem_euclid(lap);
        let bounce = if ((t * 6.0 + i as f32 * 1.7) as i32) % 2 == 0 { 0.0 } else { cell };
        let y = road.top() + 6.0 - bounce;
        paint_kart(p, pos2(x.round(), y), cell, *body, *dark);
    }
}

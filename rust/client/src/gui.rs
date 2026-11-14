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
const CARD_H: f32 = 258.0;

/// Steam Deck Game Mode (gamescope): open fullscreen.
pub fn game_mode() -> bool {
    std::env::var_os("SteamGamepadUI").is_some() || std::env::var_os("GAMESCOPE_WAYLAND_DISPLAY").is_some()
}

pub fn run(core: Arc<Core>, start_in_tray: bool) -> Result<(), String> {
    let icon = egui::IconData { rgba: crate::icon::rgba(64), width: 64, height: 64 };
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("MKWMP")
        .with_icon(Arc::new(icon))
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
            let tray = if crate::tray::available() { crate::tray::start() } else { None };
            Ok(Box::new(App::new(core, tray, start_in_tray)))
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
    /// The "add a friend" field.
    friend_entry: String,
    /// A friend waiting for "remove?" confirmation.
    confirm_unfriend: Option<String>,
    pin_entry: String,
    error: Option<String>,
    confirm_delete: bool,
    /// DELETE was confirmed; waiting for the server.
    deleting: bool,
    /// The tray icon (None: no tray on this desktop; closing quits).
    tray: Option<crate::tray::Tray>,
    /// Started with --tray: hide after the first frames.
    start_in_tray: bool,
    frames: u32,
    settings_open: bool,
}

impl App {
    fn new(core: Arc<Core>, tray: Option<crate::tray::Tray>, start_in_tray: bool) -> Self {
        let draft = core.ui().cfg;
        Self {
            core,
            draft,
            friend_entry: String::new(),
            confirm_unfriend: None,
            pin_entry: String::new(),
            error: None,
            confirm_delete: false,
            deleting: false,
            tray,
            start_in_tray,
            frames: 0,
            settings_open: false,
        }
    }

    /// Development: MKWMP_GUI_SHOT=<file.png> saves a screenshot of the window
    /// after it settled (MKWMP_GUI_SETTINGS=1 opens SETTINGS first), then quits.
    fn dev_screenshot(&mut self, ctx: &egui::Context) {
        let Some(path) = std::env::var_os("MKWMP_GUI_SHOT") else { return };
        if self.frames == 2 && std::env::var_os("MKWMP_GUI_SETTINGS").is_some() {
            self.settings_open = true;
        }
        if self.frames == 90 {
            let path = std::path::PathBuf::from(path);
            ctx.request_screenshot(move |img| {
                let rgba: Vec<u8> = img.pixels.iter().flat_map(|p| p.to_array()).collect();
                let png = crate::icon::encode_png(img.size[0] as u32, img.size[1] as u32, &rgba);
                let _ = std::fs::write(&path, png);
                crate::tray::WINDOW.quit();
            });
        }
        ctx.request_repaint();
    }

    /// The window's close button: into the tray (if there is one and the
    /// setting is on), otherwise really close.
    fn handle_close(&mut self, ctx: &egui::Context) {
        use crate::tray::WINDOW;
        if self.start_in_tray && self.tray.is_some() && self.frames == 2 {
            WINDOW.hide();
        }
        if !ctx.input(|i| i.viewport().close_requested()) || WINDOW.quitting() {
            return;
        }
        if self.tray.is_none() || !self.draft.background.close_to_tray {
            return;
        }
        ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
        self.save();
        WINDOW.hide();
        if !self.draft.background.tray_hint_shown {
            self.draft.background.tray_hint_shown = true;
            self.save();
            crate::notify::show(
                "MKWMP is still running".into(),
                "It's in the system tray, so friends can see you online. Quit from the tray icon.".into(),
            );
        }
    }

    fn save(&mut self) {
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
    fn ui(&mut self, root: &mut egui::Ui, frame: &mut eframe::Frame) {
        let ctx = root.ctx().clone();
        crate::tray::WINDOW.attach(&ctx, frame);
        self.frames = self.frames.saturating_add(1);
        self.handle_close(&ctx);
        self.dev_screenshot(&ctx);
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

        // SETTINGS, top right of the canvas.
        let gear = Rect::from_min_size(pos2(col.right() - 48.0, col.top() + 12.0), vec2(44.0, 40.0));
        if gear_button(root, gear).clicked() {
            self.settings_open = true;
        }
        if self.settings_open {
            self.settings_dialog(&ctx);
        }

        if self.confirm_delete {
            self.delete_dialog(&ctx);
        }
        if self.confirm_unfriend.is_some() {
            self.unfriend_dialog(&ctx);
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
            field_label(ui, "GAMERTAG");
            ui.horizontal(|ui| {
                let has_pin = self.draft.pin.len() == 6;
                let r = ui.add_enabled(
                    !busy,
                    TextEdit::singleline(&mut self.draft.name)
                        .hint_text("your gamertag")
                        .char_limit(16)
                        .margin(vec2(8.0, 6.0))
                        .desired_width(INNER - 214.0), // room for PIN badge + X
                );
                if r.changed() {
                    self.draft.name.retain(|ch| !ch.is_whitespace()); // gamertags have no spaces
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
                ("3-16 letters, digits, - or _ (no spaces)".to_string(), RED_D)
            } else if let RegisterView::Failed(m) = &st.register {
                (m.clone(), RED_D)
            } else if st.register == RegisterView::Taken && self.draft.pin.len() != 6 {
                ("That gamertag is registered. If it's yours, enter its PIN.".to_string(), RED_D)
            } else if self.draft.pin.len() == 6 {
                ("Your PIN: keep it to use this gamertag on another device.".to_string(), MUTED)
            } else {
                ("Register a gamertag to play. You'll get a 6-digit PIN.".to_string(), MUTED)
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
                // Picked, not typed: the field and BROWSE both open the
                // system folder dialog (Explorer on Windows).
                let field = path_field(ui, &self.draft.game, vec2(INNER - 112.0, 32.0), !busy);
                let browse = pixel_button(ui, "BROWSE", vec2(96.0, 32.0), BLUE, BLUE_D, PAPER, 9.0, !busy);
                if field.clicked() || browse.clicked() {
                    if let Some(dir) = pick_game_folder(&self.draft.game) {
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

    fn friends_card(&mut self, ui: &mut egui::Ui, st: &crate::core::Ui, _busy: bool) -> f32 {
        let min_h = CARD_H;
        let f = &st.friends;
        let signed = self.draft.pin.len() == 6 && valid_name(&self.draft.name);
        card(ui, "FRIENDS", GREEN, GREEN_D, min_h, |ui| {
            // Add by gamertag.
            ui.horizontal(|ui| {
                let r = ui.add_enabled(
                    signed,
                    TextEdit::singleline(&mut self.friend_entry)
                        .hint_text("add a friend by gamertag")
                        .char_limit(16)
                        .margin(vec2(8.0, 6.0))
                        .desired_width(INNER - 104.0),
                );
                if r.changed() {
                    self.friend_entry.retain(|ch| !ch.is_whitespace()); // gamertags have no spaces
                }
                let entry = self.friend_entry.trim().to_string();
                let ok = signed && !f.working && valid_name(&entry);
                let enter = r.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter));
                if pixel_button(ui, "ADD", vec2(80.0, 32.0), GREEN, GREEN_D, PAPER, 10.0, ok).clicked() || (enter && ok) {
                    self.core.add_friend(entry);
                    self.friend_entry.clear();
                }
            });
            // Exactly one message line (fixed card height).
            let (msg, color) = if !signed {
                ("Register your gamertag to add friends.".to_string(), MUTED)
            } else if let Some((m, err)) = &f.note {
                (m.clone(), if *err { RED_D } else { GREEN_D })
            } else if f.list.is_none() {
                ("Loading your friends…".to_string(), MUTED)
            } else {
                ("Friends are kept on the server, and are your friends in the game.".to_string(), MUTED)
            };
            hint(ui, &msg, color);

            // The lists, scrolling inside the card's fixed height.
            // What is left of the fixed card height (the card pads itself to
            // CARD_H, so measure from the cursor, not the content size).
            let h = (ui.min_rect().top() + CARD_H - ui.cursor().top() - 2.0).max(60.0);
            egui::ScrollArea::vertical().max_height(h).min_scrolled_height(h).auto_shrink([false, false]).show(ui, |ui| {
                ui.set_width(INNER - 12.0);
                ui.spacing_mut().item_spacing.y = 2.0;
                let Some(list) = &f.list else { return };
                if list.friends.is_empty() && list.incoming.is_empty() && list.outgoing.is_empty() {
                    ui.add_space(6.0);
                    hint(ui, "No friends yet. Add someone by their gamertag above.", MUTED);
                    return;
                }
                if !list.incoming.is_empty() {
                    section(ui, &format!("REQUESTS · {}", list.incoming.len()));
                    for r in &list.incoming {
                        ui.push_id(("in", &r.name), |ui| {
                            friend_row(ui, YELLOW, &r.name, "wants to be friends", |ui| {
                                if small_button(ui, "X", 24.0, RED, RED_D, !f.working).on_hover_text("Decline").clicked() {
                                    self.core.remove_friend(r.name.clone(), "reject");
                                }
                                if small_button(ui, "ACCEPT", 70.0, GREEN, GREEN_D, !f.working).clicked() {
                                    self.core.accept_friend(r.name.clone());
                                }
                            });
                        });
                    }
                }
                if !list.friends.is_empty() {
                    let on = list.friends.iter().filter(|x| x.state != "offline").count();
                    section(ui, &format!("FRIENDS · {on} ONLINE"));
                    for fr in &list.friends {
                        let (light_c, text) = match fr.state.as_str() {
                            "playing" => (GREEN, "playing"),
                            "online" => (BLUE, "online"),
                            _ => (Color32::from_rgb(0xc9, 0xc1, 0xae), "offline"),
                        };
                        ui.push_id(("fr", &fr.name), |ui| {
                            friend_row(ui, light_c, &fr.name, text, |ui| {
                                if small_button(ui, "X", 24.0, RED, RED_D, !f.working).on_hover_text("Remove friend").clicked() {
                                    self.confirm_unfriend = Some(fr.name.clone());
                                }
                            });
                        });
                    }
                }
                if !list.outgoing.is_empty() {
                    section(ui, &format!("SENT · {}", list.outgoing.len()));
                    for r in &list.outgoing {
                        ui.push_id(("out", &r.name), |ui| {
                            friend_row(ui, PAPER, &r.name, "request sent", |ui| {
                                if small_button(ui, "X", 24.0, RED, RED_D, !f.working).on_hover_text("Cancel request").clicked() {
                                    self.core.remove_friend(r.name.clone(), "cancel");
                                }
                            });
                        });
                    }
                }
            });
        })
    }

    fn settings_dialog(&mut self, ctx: &egui::Context) {
        let has_tray = self.tray.is_some();
        let r = pixel_modal("settings").show(ctx, |ui| {
            ui.set_width(440.0);
            ui.label(RichText::new("SETTINGS").font(pixel(13.0)).color(INK));
            ui.add_space(8.0);
            let b = &mut self.draft.background;
            let mut changed = false;
            section(ui, "IN THE BACKGROUND");
            ui.add_enabled_ui(has_tray, |ui| {
                changed |= ui.checkbox(&mut b.close_to_tray, "Keep running in the system tray when I close the window").changed();
            });
            if !has_tray {
                hint(ui, "No system tray here (Steam Deck Game Mode): closing quits.", MUTED);
            }
            if crate::autostart::supported() {
                if ui.checkbox(&mut b.start_with_system, "Start with my computer, in the tray").changed() {
                    changed = true;
                    if let Err(e) = crate::autostart::apply(b.start_with_system) {
                        self.error = Some(e);
                    }
                }
            }
            hint(ui, "Running in the background never takes a seat: you get one only when you play.", MUTED);
            ui.add_space(6.0);
            section(ui, "NOTIFY ME WHEN");
            changed |= ui.checkbox(&mut b.notify_online, "a friend comes online").changed();
            changed |= ui.checkbox(&mut b.notify_playing, "a friend starts playing").changed();
            changed |= ui.checkbox(&mut b.notify_requests, "someone sends me a friend request").changed();
            if changed {
                self.save();
            }
            ui.add_space(12.0);
            ui.horizontal(|ui| {
                if pixel_button(ui, "TEST", vec2(150.0, 36.0), YELLOW, YELLOW_D, INK, 11.0, true)
                    .on_hover_text("Show a sample notification")
                    .clicked()
                {
                    crate::notify::show("Ana is online".into(), "This is how news about your friends will look.".into());
                }
                ui.add_space(8.0);
                if pixel_button(ui, "DONE", vec2(150.0, 36.0), BLUE, BLUE_D, PAPER, 11.0, true).clicked() {
                    self.settings_open = false;
                }
            });
        });
        if r.should_close() {
            self.settings_open = false;
        }
    }

    fn unfriend_dialog(&mut self, ctx: &egui::Context) {
        let name = self.confirm_unfriend.clone().unwrap_or_default();
        let r = pixel_modal("remove-friend").show(ctx, |ui| {
            ui.set_width(360.0);
            ui.label(RichText::new("REMOVE FRIEND?").font(pixel(11.0)).color(INK));
            ui.add_space(6.0);
            ui.label(
                RichText::new(format!("{name} will no longer be your friend, here or in the game."))
                    .size(13.0)
                    .color(MUTED),
            );
            ui.add_space(10.0);
            ui.horizontal(|ui| {
                if pixel_button(ui, "REMOVE", vec2(150.0, 36.0), RED, RED_D, PAPER, 11.0, true).clicked() {
                    self.confirm_unfriend = None;
                    self.core.remove_friend(name.clone(), "remove");
                }
                ui.add_space(8.0);
                if pixel_button(ui, "CANCEL", vec2(150.0, 36.0), BLUE, BLUE_D, PAPER, 11.0, true).clicked() {
                    self.confirm_unfriend = None;
                }
            });
        });
        if r.should_close() {
            self.confirm_unfriend = None;
        }
    }

    fn delete_dialog(&mut self, ctx: &egui::Context) {
        let r = pixel_modal("delete-profile").show(ctx, |ui| {
            ui.set_width(360.0);
            ui.label(RichText::new("DELETE YOUR PROFILE FROM SERVER?").font(pixel(11.0)).color(INK));
            ui.add_space(6.0);
            ui.label(
                RichText::new(format!(
                    "\"{}\" and its PIN will be removed. Anyone can then register this gamertag.",
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

const GEAR: [&str; 7] = ["..#.#..", ".#####.", "##...##", ".#...#.", "##...##", ".#####.", "..#.#.."];

/// The square SETTINGS button (a gear), placed at `rect`.
fn gear_button(ui: &mut egui::Ui, rect: Rect) -> Response {
    let resp = ui.interact(rect, ui.scope_id().with("settings"), Sense::click());
    let pressed = resp.is_pointer_button_down_on();
    let r = if pressed { rect.translate(vec2(3.0, 3.0)) } else { rect };
    let p = ui.painter();
    if !pressed {
        p.rect_filled(rect.translate(vec2(3.0, 3.0)), 0.0, INK);
    }
    let fill = if resp.hovered() { PAPER } else { YELLOW };
    p.rect_filled(r, 0.0, fill);
    p.rect_filled(Rect::from_min_max(pos2(r.left(), r.bottom() - 3.0), r.max), 0.0, YELLOW_D);
    p.rect_stroke(r, 0.0, Stroke::new(3.0, INK), StrokeKind::Inside);
    let cell = 3.0;
    paint_icon(p, r.center() - vec2(3.5 * cell, 3.5 * cell), &GEAR, cell, INK);
    if resp.hovered() {
        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
    }
    resp.on_hover_text("Settings: tray, notifications, start-up")
}

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

/// The system folder dialog, opened at the current game folder.
fn pick_game_folder(current: &std::path::Path) -> Option<std::path::PathBuf> {
    let mut d = rfd::FileDialog::new().set_title("Game folder (with mkw.exe)");
    if current.is_dir() {
        d = d.set_directory(current);
    }
    d.pick_folder()
}

/// A read-only, clickable path box styled like the text fields. Long paths
/// keep their end (the folder name) and lose their start.
fn path_field(ui: &mut egui::Ui, path: &std::path::Path, size: Vec2, enabled: bool) -> Response {
    let sense = if enabled { Sense::click() } else { Sense::hover() };
    let (rect, resp) = ui.allocate_exact_size(size, sense);
    let resp = resp.on_hover_cursor(if enabled { egui::CursorIcon::PointingHand } else { egui::CursorIcon::Default });
    let v = ui.visuals();
    let stroke = if enabled && resp.hovered() { v.widgets.hovered.bg_stroke } else { v.widgets.inactive.bg_stroke };
    let p = ui.painter();
    p.rect(rect, CornerRadius::ZERO, Color32::WHITE, stroke, egui::StrokeKind::Inside);
    let font = egui::TextStyle::Body.resolve(ui.style());
    let max_w = rect.width() - 16.0;
    let full = path.display().to_string();
    let (mut text, color) = if full.is_empty() { ("click to choose the folder with mkw.exe".to_string(), Color32::GRAY) } else { (full, INK) };
    let fits = |t: &str| p.layout_no_wrap(t.to_string(), font.clone(), color).size().x <= max_w;
    if !fits(&text) {
        let chars: Vec<char> = text.chars().collect();
        let mut start = 1;
        while start < chars.len() && !fits(&format!("…{}", chars[start..].iter().collect::<String>())) {
            start += 1;
        }
        text = format!("…{}", chars[start..].iter().collect::<String>());
    }
    let color = if enabled { color } else { color.gamma_multiply(0.5) };
    p.text(rect.left_center() + vec2(8.0, 0.0), egui::Align2::LEFT_CENTER, text, font, color);
    resp
}

/// The confirmation dialog frame (paper, ink border, hard shadow).
fn pixel_modal(id: &str) -> egui::Modal {
    egui::Modal::new(egui::Id::unique(id)).backdrop_color(Color32::from_black_alpha(120)).frame(
        egui::Frame::new()
            .fill(PAPER)
            .stroke(Stroke::new(4.0, INK))
            .shadow(Shadow { offset: [8, 8], blur: 0, spread: 0, color: Color32::from_black_alpha(120) })
            .inner_margin(20),
    )
}

/// A small heading inside a card list.
fn section(ui: &mut egui::Ui, text: &str) {
    ui.add_space(4.0);
    ui.label(RichText::new(text).font(pixel(8.0)).color(MUTED));
}

/// One list row: a state light, the name, a muted note, and buttons on the right.
fn friend_row(ui: &mut egui::Ui, color: Color32, name: &str, note: &str, buttons: impl FnOnce(&mut egui::Ui)) {
    ui.horizontal(|ui| {
        ui.set_height(28.0);
        light(ui, color);
        ui.label(RichText::new(name).size(14.0).color(INK).strong());
        ui.label(RichText::new(note).size(12.0).color(MUTED));
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.spacing_mut().item_spacing.x = 8.0;
            buttons(ui);
        });
    });
}

/// A compact pixel button for list rows.
fn small_button(ui: &mut egui::Ui, text: &str, w: f32, fill: Color32, dark: Color32, enabled: bool) -> Response {
    pixel_button(ui, text, vec2(w, 22.0), fill, dark, PAPER, 8.0, enabled)
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
        .on_hover_text("Your gamertag's PIN. It's also in client.toml.");
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

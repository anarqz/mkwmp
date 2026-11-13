//! The launcher window (egui): server status, name, server URL, game folder,
//! friends, PLAY. 16-bit racing look to match the project site.

use std::sync::Arc;
use std::time::Duration;

use eframe::egui::{
    self, Align, Color32, CornerRadius, FontData, FontDefinitions, FontFamily, FontId, Layout, RichText, Stroke,
    TextEdit, Vec2,
};

use crate::config::{valid_name, ClientConfig};
use crate::core::{Core, ProfileView, ServerView, SessionView};
use crate::seat::Phase;
use crate::update::VERSION;

const INK: Color32 = Color32::from_rgb(0x1b, 0x15, 0x30);
const PANEL: Color32 = Color32::from_rgb(0x2a, 0x1f, 0x4f);
const PAPER: Color32 = Color32::from_rgb(0xff, 0xf8, 0xe6);
const RED: Color32 = Color32::from_rgb(0xe6, 0x00, 0x12);
const YELLOW: Color32 = Color32::from_rgb(0xff, 0xd4, 0x00);
const BLUE: Color32 = Color32::from_rgb(0x0a, 0x6c, 0xff);
const GREEN: Color32 = Color32::from_rgb(0x2f, 0xb5, 0x2f);
const GREY: Color32 = Color32::from_rgb(0x8a, 0x84, 0xa3);
const SKY: Color32 = Color32::from_rgb(0xa6, 0xe3, 0xff);

/// Steam Deck Game Mode (gamescope): bigger UI, fullscreen.
pub fn game_mode() -> bool {
    std::env::var_os("SteamGamepadUI").is_some() || std::env::var_os("GAMESCOPE_WAYLAND_DISPLAY").is_some()
}

pub fn run(core: Arc<Core>) -> Result<(), String> {
    let big = game_mode();
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("MKWMP")
        .with_inner_size([500.0, 760.0])
        .with_min_inner_size([420.0, 560.0]);
    if big {
        viewport = viewport.with_fullscreen(true);
    }
    let options = eframe::NativeOptions { viewport, ..Default::default() };
    eframe::run_native(
        "MKWMP",
        options,
        Box::new(move |cc| {
            setup_style(&cc.egui_ctx, big);
            Ok(Box::new(App::new(core)))
        }),
    )
    .map_err(|e| format!("window: {e}"))
}

fn setup_style(ctx: &egui::Context, big: bool) {
    let mut fonts = FontDefinitions::default();
    fonts.font_data.insert(
        "pixel".into(),
        Arc::new(FontData::from_static(include_bytes!("../assets/PressStart2P-Regular.ttf"))),
    );
    fonts.families.insert(FontFamily::Name("pixel".into()), vec!["pixel".into()]);
    ctx.set_fonts(fonts);

    let mut v = egui::Visuals::dark();
    v.panel_fill = INK;
    v.window_fill = INK;
    v.extreme_bg_color = PANEL;
    v.override_text_color = Some(PAPER);
    v.widgets.inactive.bg_fill = PANEL;
    v.widgets.inactive.weak_bg_fill = PANEL;
    v.widgets.hovered.weak_bg_fill = Color32::from_rgb(0x3a, 0x2c, 0x6a);
    v.selection.bg_fill = BLUE;
    v.selection.stroke = Stroke::new(1.0, PAPER);
    ctx.set_visuals(v);
    ctx.all_styles_mut(|s| {
        s.spacing.item_spacing = Vec2::new(8.0, 8.0);
        s.spacing.interact_size.y = 30.0;
        for f in s.text_styles.values_mut() {
            f.size += 2.0;
        }
    });
    if big {
        ctx.set_zoom_factor(1.6);
    }
}

fn pixel(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name("pixel".into()))
}

struct App {
    core: Arc<Core>,
    draft: ClientConfig,
    checked_name: String,
    friends: [String; 8],
    error: Option<String>,
}

impl App {
    fn new(core: Arc<Core>) -> Self {
        let draft = core.ui().cfg;
        let mut friends: [String; 8] = Default::default();
        for (slot, f) in friends.iter_mut().zip(draft.friends.iter()) {
            *slot = f.clone();
        }
        Self { core, draft, friends, error: None, checked_name: String::new() }
    }

    fn save(&mut self) {
        self.draft.friends = self.friends.iter().map(|f| f.trim().to_string()).filter(|f| !f.is_empty()).collect();
        if self.draft != self.core.ui().cfg {
            self.error = self.core.set_config(self.draft.clone()).err();
        }
    }
}

impl eframe::App for App {
    fn ui(&mut self, root: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = root.ctx().clone();
        ctx.request_repaint_after(Duration::from_millis(400));
        let ui_state = self.core.ui();
        let busy = !matches!(ui_state.session, SessionView::Idle | SessionView::Failed(_));

        egui::CentralPanel::default().show(root, |ui| {
            ui.add_space(6.0);
            // ---- title -------------------------------------------------------
            ui.horizontal(|ui| {
                ui.label(RichText::new("MKW").font(pixel(34.0)).color(PAPER));
                ui.add_space(-8.0);
                ui.label(RichText::new("M").font(pixel(34.0)).color(RED));
                ui.add_space(-8.0);
                ui.label(RichText::new("P").font(pixel(34.0)).color(YELLOW));
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    ui.label(RichText::new(format!("v{VERSION}")).font(pixel(9.0)).color(GREY));
                });
            });
            checkered(ui);

            // ---- server status ---------------------------------------------
            framed(ui, PANEL, |ui| {
                ui.horizontal(|ui| {
                    let (dot, text) = match &ui_state.server {
                        ServerView::Checking => (GREY, "checking the server…".to_string()),
                        ServerView::Online { name, seats_total, seated, queued } => (
                            GREEN,
                            format!(
                                "{} · ONLINE · {}/{} seats free{}",
                                name.to_uppercase(),
                                seats_total - seated,
                                seats_total,
                                if *queued > 0 { format!(" · {queued} in queue") } else { String::new() }
                            ),
                        ),
                        ServerView::Offline(_) => (RED, "server offline".to_string()),
                    };
                    dot_mark(ui, dot);
                    ui.label(RichText::new(text).font(pixel(10.0)).color(SKY));
                });
            });

            // ---- settings ----------------------------------------------------
            ui.add_space(4.0);
            heading(ui, "PLAYER", BLUE);
            egui::Grid::new("settings").num_columns(2).spacing([10.0, 8.0]).show(ui, |ui| {
                ui.label("Name");
                let ok = valid_name(&self.draft.name);
                let r = ui.add_enabled(
                    !busy,
                    TextEdit::singleline(&mut self.draft.name).hint_text("3-16: letters, digits, - _").desired_width(f32::INFINITY),
                );
                if r.changed() {
                    self.draft.pin.clear(); // a PIN belongs to one name
                }
                if r.lost_focus() {
                    self.save();
                }
                ui.end_row();
                if !ok && !self.draft.name.is_empty() {
                    ui.label("");
                    ui.label(RichText::new("3-16 letters, digits, - or _").color(YELLOW).small());
                    ui.end_row();
                }
                // The profile behind the name: register it, or prove it's yours.
                if ok && !busy {
                    if self.checked_name != self.draft.name && !r.has_focus() {
                        self.checked_name = self.draft.name.clone();
                        self.save();
                        self.core.check_profile();
                    }
                    let (for_name, view) = &ui_state.profile;
                    let current = for_name.eq_ignore_ascii_case(&self.draft.name);
                    ui.label("");
                    ui.horizontal(|ui| match view {
                        _ if !current => {}
                        ProfileView::Checking => {
                            ui.label(RichText::new("checking the name…").small().color(GREY));
                        }
                        ProfileView::Available => {
                            ui.label(RichText::new("This name is free.").small().color(GREEN));
                            let b = egui::Button::new(RichText::new("REGISTER").font(pixel(10.0)).color(INK)).fill(YELLOW);
                            if ui.add(b).clicked() {
                                self.save();
                                self.core.register();
                            }
                        }
                        ProfileView::Reserved => {
                            ui.label(RichText::new("This name is reserved. Ask the server admin for its PIN.").small().color(YELLOW));
                        }
                        ProfileView::Registered | ProfileView::Unknown => {}
                    });
                    ui.end_row();
                    let registered = current && matches!(view, ProfileView::Registered | ProfileView::Reserved);
                    if registered || !self.draft.pin.is_empty() {
                        ui.label("PIN");
                        ui.horizontal(|ui| {
                            let r = ui.add(
                                TextEdit::singleline(&mut self.draft.pin)
                                    .password(true)
                                    .char_limit(6)
                                    .hint_text("6 digits")
                                    .desired_width(110.0),
                            );
                            if r.changed() {
                                self.draft.pin.retain(|c| c.is_ascii_digit());
                            }
                            if r.lost_focus() {
                                self.save();
                            }
                            let note = if self.draft.pin.len() == 6 { "saved on this device" } else { "the 6 digits you got when you registered" };
                            ui.label(RichText::new(note).small().color(GREY));
                        });
                        ui.end_row();
                    }
                }

                ui.label("Server");
                let r = ui.add_enabled(!busy, TextEdit::singleline(&mut self.draft.server).desired_width(f32::INFINITY));
                if r.lost_focus() {
                    self.save();
                }
                ui.end_row();

                ui.label("Game");
                ui.horizontal(|ui| {
                    let mut path = self.draft.game.display().to_string();
                    let w = ui.available_width() - if cfg!(windows) { 80.0 } else { 0.0 };
                    let r = ui.add_enabled(
                        !busy,
                        TextEdit::singleline(&mut path).hint_text("folder with mkw.exe").desired_width(w),
                    );
                    if r.changed() {
                        self.draft.game = path.into();
                    }
                    if r.lost_focus() {
                        self.save();
                    }
                    #[cfg(windows)]
                    if ui.add_enabled(!busy, egui::Button::new("Browse")).clicked() {
                        if let Some(dir) = rfd::FileDialog::new().set_title("Game folder (with mkw.exe)").pick_folder() {
                            self.draft.game = dir;
                            self.save();
                        }
                    }
                });
                ui.end_row();

                ui.label("");
                let mut steam = !self.draft.launch;
                if ui
                    .add_enabled(!busy, egui::Checkbox::new(&mut steam, "I start the game from Steam (Steam Deck)"))
                    .changed()
                {
                    self.draft.launch = !steam;
                    self.save();
                }
                ui.end_row();
            });

            // ---- a PIN just issued ----------------------------------------
            if let Some(pin) = &ui_state.new_pin {
                let pin = pin.clone();
                framed(ui, YELLOW, |ui| {
                    ui.label(RichText::new("YOUR PIN").font(pixel(10.0)).color(INK));
                    ui.label(RichText::new(&pin).font(pixel(26.0)).color(INK));
                    ui.label(
                        RichText::new("Write it down. You need it to use this name on another device. It won't be shown again.")
                            .color(INK),
                    );
                    ui.horizontal(|ui| {
                        if ui.button("Copy").clicked() {
                            ui.ctx().copy_text(pin.clone());
                        }
                        if ui.button("I wrote it down").clicked() {
                            self.core.dismiss_pin();
                        }
                    });
                });
                self.draft.pin = pin;
            }

            // ---- friends -----------------------------------------------------
            ui.add_space(4.0);
            heading(ui, "FRIENDS", GREEN);
            ui.label(RichText::new("Up to 8. Friends who add you back show up in the game.").small().color(GREY));
            egui::Grid::new("friends").num_columns(4).spacing([6.0, 6.0]).show(ui, |ui| {
                for row in 0..4 {
                    for col in 0..2 {
                        let i = row * 2 + col;
                        let name = self.friends[i].trim().to_ascii_lowercase();
                        let live = ui_state.friends.iter().find(|f| f.name.to_ascii_lowercase() == name);
                        let (dot, tip) = match live {
                            Some(f) if f.mutual => (GREEN, "online, friends in game"),
                            Some(f) if f.online => (YELLOW, "online, hasn't added you"),
                            Some(_) => (GREY, "offline"),
                            None if busy && !name.is_empty() => (GREY, "offline"),
                            None => (PANEL, ""),
                        };
                        dot_mark(ui, dot).on_hover_text(tip);
                        let w = (ui.available_width().min(460.0) - 40.0) / 2.0;
                        let r = ui.add_sized([w.max(120.0), 28.0], TextEdit::singleline(&mut self.friends[i]).char_limit(16));
                        if r.lost_focus() {
                            self.save();
                        }
                    }
                    ui.end_row();
                }
            });

            // ---- play ----------------------------------------------------------
            ui.add_space(10.0);
            let problem = self.draft.problem();
            let (label, color) = if busy { ("STOP", BLUE) } else { ("PLAY", RED) };
            let button = egui::Button::new(RichText::new(label).font(pixel(26.0)).color(PAPER))
                .fill(color)
                .stroke(Stroke::new(4.0, PAPER))
                .corner_radius(CornerRadius::ZERO)
                .min_size(Vec2::new(ui.available_width(), 64.0));
            let enabled = busy || problem.is_none();
            if ui.add_enabled(enabled, button).clicked() || (enabled && !busy && ui.input(|i| i.key_pressed(egui::Key::Enter))) {
                if busy {
                    self.core.stop();
                } else {
                    self.save();
                    self.core.play();
                }
            }

            // ---- what's happening ----------------------------------------
            let (line, c) = match &ui_state.session {
                SessionView::Idle => match &problem {
                    Some(p) => (p.clone(), YELLOW),
                    None => ("Ready".into(), PAPER),
                },
                SessionView::Starting => ("Connecting…".into(), SKY),
                SessionView::Failed(e) => (e.clone(), RED),
                SessionView::Running { phase, game_running, steam } => match phase {
                    Phase::Refused(m) => (m.clone(), RED),
                    Phase::Joining => ("Taking a seat…".into(), SKY),
                    Phase::Queued(n) => (format!("Server full: you are #{n} in the queue"), YELLOW),
                    Phase::WaitingForGame if *steam && !game_running => ("Start the game from Steam".into(), SKY),
                    Phase::WaitingForGame => ("In game: pick Online Play to take a seat".into(), SKY),
                    Phase::Seated if *steam && !game_running => ("Seated: start the game from Steam".into(), GREEN),
                    Phase::Seated => ("Seated: have fun!".into(), GREEN),
                },
            };
            ui.add_space(4.0);
            ui.vertical_centered(|ui| ui.label(RichText::new(line).font(pixel(10.0)).color(c)));
            if let Some(e) = &self.error {
                ui.label(RichText::new(e).color(RED).small());
            }

            // ---- footer --------------------------------------------------------
            ui.with_layout(Layout::bottom_up(Align::Min), |ui| {
                ui.label(RichText::new(&ui_state.update).small().color(GREY));
                for line in crate::log::recent(3).into_iter().rev() {
                    ui.label(RichText::new(line).small().color(GREY));
                }
            });
        });
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.save();
        self.core.stop();
    }
}

/// A round status light (the UI font has no bullet glyph).
fn dot_mark(ui: &mut egui::Ui, color: Color32) -> egui::Response {
    let (rect, resp) = ui.allocate_exact_size(Vec2::splat(14.0), egui::Sense::hover());
    ui.painter().circle_filled(rect.center(), 5.0, color);
    resp
}

fn heading(ui: &mut egui::Ui, text: &str, color: Color32) {
    ui.label(RichText::new(text).font(pixel(12.0)).color(color));
}

fn framed(ui: &mut egui::Ui, fill: Color32, add: impl FnOnce(&mut egui::Ui)) {
    egui::Frame::new()
        .fill(fill)
        .stroke(Stroke::new(3.0, Color32::BLACK))
        .inner_margin(10.0)
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            add(ui);
        });
}

/// A strip of the checkered flag.
fn checkered(ui: &mut egui::Ui) {
    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 12.0), egui::Sense::hover());
    let p = ui.painter();
    let n = (rect.width() / 12.0) as i32;
    for i in 0..n {
        for j in 0..2 {
            let c = if (i + j) % 2 == 0 { PAPER } else { Color32::BLACK };
            let min = rect.min + Vec2::new(i as f32 * 12.0, j as f32 * 6.0);
            p.rect_filled(egui::Rect::from_min_size(min, Vec2::new(12.0, 6.0)), 0.0, c);
        }
    }
}

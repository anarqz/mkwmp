//! The system tray: closing the window keeps the launcher running there
//! (friends list current, notifications on), without holding a seat.
//!
//! - Windows / macOS: `tray-icon` (left click opens; menu: Open, Quit).
//! - Linux (KDE / Steam Deck desktop mode): a StatusNotifierItem via `ksni`.
//! - Steam Deck Game Mode has no tray: the window simply stays a window.
//!
//! [`WINDOW`] hides and shows the launcher window from any thread. On Windows
//! it drives the native window directly (a hidden eframe window does not run
//! its frame loop, so viewport commands alone could not bring it back);
//! elsewhere it minimizes.

use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::OnceLock;

use eframe::egui;

pub struct Window {
    ctx: OnceLock<egui::Context>,
    /// Windows: the native window handle (HWND), 0 until the first frame.
    #[cfg_attr(not(windows), allow(dead_code))]
    hwnd: AtomicIsize,
    quitting: AtomicBool,
}

pub static WINDOW: Window =
    Window { ctx: OnceLock::new(), hwnd: AtomicIsize::new(0), quitting: AtomicBool::new(false) };

impl Window {
    /// Called every frame by the window: remembers its context and handle.
    pub fn attach(&self, ctx: &egui::Context, frame: &eframe::Frame) {
        let _ = self.ctx.set(ctx.clone());
        #[cfg(windows)]
        if self.hwnd.load(Ordering::Relaxed) == 0 {
            use raw_window_handle::{HasWindowHandle, RawWindowHandle};
            if let Ok(h) = frame.window_handle() {
                if let RawWindowHandle::Win32(w) = h.as_raw() {
                    self.hwnd.store(w.hwnd.get(), Ordering::Relaxed);
                }
            }
        }
        #[cfg(not(windows))]
        let _ = frame;
    }

    pub fn quitting(&self) -> bool {
        self.quitting.load(Ordering::Relaxed)
    }

    pub fn hide(&self) {
        #[cfg(windows)]
        {
            let hwnd = self.hwnd.load(Ordering::Relaxed);
            if hwnd != 0 {
                use windows_sys::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_HIDE};
                unsafe { ShowWindow(hwnd as _, SW_HIDE) };
                return;
            }
        }
        if let Some(ctx) = self.ctx.get() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(true));
        }
    }

    pub fn show(&self) {
        #[cfg(windows)]
        {
            let hwnd = self.hwnd.load(Ordering::Relaxed);
            if hwnd != 0 {
                use windows_sys::Win32::UI::WindowsAndMessaging::{SetForegroundWindow, ShowWindow, SW_RESTORE, SW_SHOW};
                unsafe {
                    ShowWindow(hwnd as _, SW_SHOW);
                    ShowWindow(hwnd as _, SW_RESTORE);
                    SetForegroundWindow(hwnd as _);
                }
            }
        }
        if let Some(ctx) = self.ctx.get() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
            ctx.request_repaint();
        }
    }

    /// Quit for real (tray menu): the window closes and the launcher signs out.
    pub fn quit(&self) {
        self.quitting.store(true, Ordering::Relaxed);
        self.show();
        if let Some(ctx) = self.ctx.get() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            ctx.request_repaint();
        }
    }
}

/// Whether this desktop has a tray to hide into.
pub fn available() -> bool {
    !crate::gui::game_mode()
}

/// The tray icon. Keep it alive as long as the window (dropping removes it).
pub struct Tray {
    #[cfg(any(windows, target_os = "macos"))]
    _icon: tray_icon::TrayIcon,
}

#[cfg(any(windows, target_os = "macos"))]
pub fn start() -> Option<Tray> {
    use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
    use tray_icon::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};

    let open = MenuItem::new("Open MKWMP", true, None);
    let quit = MenuItem::new("Quit", true, None);
    let menu = Menu::new();
    let ok = menu.append(&open).and_then(|_| menu.append(&PredefinedMenuItem::separator())).and_then(|_| menu.append(&quit));
    if let Err(e) = ok {
        crate::log::note(format!("tray menu: {e}"));
        return None;
    }
    let (open_id, quit_id) = (open.id().clone(), quit.id().clone());
    MenuEvent::set_event_handler(Some(move |e: MenuEvent| {
        if e.id == open_id {
            WINDOW.show();
        } else if e.id == quit_id {
            WINDOW.quit();
        }
    }));
    TrayIconEvent::set_event_handler(Some(|e: TrayIconEvent| {
        if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = e {
            WINDOW.show();
        }
    }));
    let icon = match tray_icon::Icon::from_rgba(crate::icon::rgba(32), 32, 32) {
        Ok(i) => i,
        Err(e) => {
            crate::log::note(format!("tray icon: {e}"));
            return None;
        }
    };
    match TrayIconBuilder::new()
        .with_icon(icon)
        .with_tooltip("MKWMP")
        .with_menu(Box::new(menu))
        .with_menu_on_left_click(false)
        .build()
    {
        Ok(t) => Some(Tray { _icon: t }),
        Err(e) => {
            crate::log::note(format!("tray: {e}"));
            None
        }
    }
}

#[cfg(target_os = "linux")]
pub fn start() -> Option<Tray> {
    use ksni::TrayMethods;

    struct Sni;
    impl ksni::Tray for Sni {
        fn id(&self) -> String {
            "mkwmp-launcher".into()
        }
        fn title(&self) -> String {
            "MKWMP".into()
        }
        fn icon_pixmap(&self) -> Vec<ksni::Icon> {
            // RGBA -> ARGB, network byte order.
            let mut data = crate::icon::rgba(32);
            for px in data.chunks_mut(4) {
                px.rotate_right(1);
            }
            vec![ksni::Icon { width: 32, height: 32, data }]
        }
        fn activate(&mut self, _x: i32, _y: i32) {
            WINDOW.show();
        }
        fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
            use ksni::menu::StandardItem;
            vec![
                StandardItem { label: "Open MKWMP".into(), activate: Box::new(|_: &mut Self| WINDOW.show()), ..Default::default() }.into(),
                ksni::MenuItem::Separator,
                StandardItem { label: "Quit".into(), activate: Box::new(|_: &mut Self| WINDOW.quit()), ..Default::default() }.into(),
            ]
        }
    }
    let rt = tokio::runtime::Handle::try_current().ok()?;
    rt.spawn(async {
        match Sni.spawn().await {
            // Keep the handle (and the tray) for the whole run.
            Ok(handle) => std::mem::forget(handle),
            Err(e) => crate::log::note(format!("tray: {e} (no StatusNotifier host?)")),
        }
    });
    Some(Tray {})
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn start() -> Option<Tray> {
    None
}

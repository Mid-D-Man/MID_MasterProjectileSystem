// config_store.rs
// Per-config movement parameter store for Wave and Circular movement types.
//
// Platform dispatch — following glam's cfg pattern:
//   Non-WASM (desktop/mobile): RwLock<HashMap> — thread-safe, lock-free reads after init.
//   WASM (WebGL): thread_local! + RefCell — WASM is single-threaded, no std::sync available.
//   scalar-math feature: same as WASM path for maximum compatibility.
//
// Registration happens main-thread only at startup.
// Tick reads are read-only after registration.
// The cfg selection is resolved at compile time — zero runtime branching.

// ── Param types (platform-independent) ───────────────────────────────────────

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct WaveParams {
    pub amplitude:    f32,
    pub frequency:    f32,
    pub phase_offset: f32,
    pub vertical:     bool,
    pub _pad:         [u8; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct CircularParams {
    pub radius:          f32,
    pub angular_speed:   f32,
    pub start_angle_deg: f32,
}

// ── Platform-specific store implementation ────────────────────────────────────

#[cfg(all(
    not(target_arch = "wasm32"),
    not(feature = "scalar-math")
))]
mod store_impl {
    use super::{WaveParams, CircularParams};
    use std::collections::HashMap;
    use std::sync::RwLock;

    lazy_static::lazy_static! {
        static ref WAVE:     RwLock<HashMap<u16, WaveParams>>     = RwLock::new(HashMap::new());
        static ref CIRCULAR: RwLock<HashMap<u16, CircularParams>> = RwLock::new(HashMap::new());
    }

    pub fn reg_wave(id: u16, p: WaveParams) {
        if let Ok(mut m) = WAVE.write() { m.insert(id, p); }
    }
    pub fn reg_circular(id: u16, p: CircularParams) {
        if let Ok(mut m) = CIRCULAR.write() { m.insert(id, p); }
    }
    pub fn unreg_wave(id: u16) {
        if let Ok(mut m) = WAVE.write() { m.remove(&id); }
    }
    pub fn unreg_circular(id: u16) {
        if let Ok(mut m) = CIRCULAR.write() { m.remove(&id); }
    }
    pub fn clear() {
        if let Ok(mut m) = WAVE.write()     { m.clear(); }
        if let Ok(mut m) = CIRCULAR.write() { m.clear(); }
    }
    pub fn get_wave(id: u16) -> Option<WaveParams> {
        WAVE.read().ok()?.get(&id).copied()
    }
    pub fn get_circular(id: u16) -> Option<CircularParams> {
        CIRCULAR.read().ok()?.get(&id).copied()
    }
}

// WASM / scalar-math: single-threaded, use thread_local RefCell.
// No RwLock, no lazy_static — both require threading support absent in WASM.
#[cfg(any(
    target_arch = "wasm32",
    feature = "scalar-math"
))]
mod store_impl {
    use super::{WaveParams, CircularParams};
    use std::collections::HashMap;
    use std::cell::RefCell;

    thread_local! {
        static WAVE:     RefCell<HashMap<u16, WaveParams>>     = RefCell::new(HashMap::new());
        static CIRCULAR: RefCell<HashMap<u16, CircularParams>> = RefCell::new(HashMap::new());
    }

    pub fn reg_wave(id: u16, p: WaveParams) {
        WAVE.with(|m| m.borrow_mut().insert(id, p));
    }
    pub fn reg_circular(id: u16, p: CircularParams) {
        CIRCULAR.with(|m| m.borrow_mut().insert(id, p));
    }
    pub fn unreg_wave(id: u16) {
        WAVE.with(|m| m.borrow_mut().remove(&id));
    }
    pub fn unreg_circular(id: u16) {
        CIRCULAR.with(|m| m.borrow_mut().remove(&id));
    }
    pub fn clear() {
        WAVE.with(|m| m.borrow_mut().clear());
        CIRCULAR.with(|m| m.borrow_mut().clear());
    }
    pub fn get_wave(id: u16) -> Option<WaveParams> {
        WAVE.with(|m| m.borrow().get(&id).copied())
    }
    pub fn get_circular(id: u16) -> Option<CircularParams> {
        CIRCULAR.with(|m| m.borrow().get(&id).copied())
    }
}

// ── Public API (delegates to platform impl) ───────────────────────────────────

pub fn register_wave(id: u16, p: WaveParams)     { store_impl::reg_wave(id, p); }
pub fn register_circular(id: u16, p: CircularParams) { store_impl::reg_circular(id, p); }
pub fn unregister_wave(id: u16)                  { store_impl::unreg_wave(id); }
pub fn unregister_circular(id: u16)              { store_impl::unreg_circular(id); }
pub fn clear_all()                               { store_impl::clear(); }

#[inline(always)]
pub fn get_wave(id: u16)     -> Option<WaveParams>     { store_impl::get_wave(id) }
#[inline(always)]
pub fn get_circular(id: u16) -> Option<CircularParams> { store_impl::get_circular(id) }

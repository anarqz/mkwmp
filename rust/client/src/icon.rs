//! The launcher's icon (window, tray, notifications), drawn in code in the
//! site's pixel style: a red kart on a yellow coin tile with an ink border.

/// The kart sprite (same as the window's road), facing right.
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

const INK: [u8; 4] = [0x1b, 0x15, 0x30, 0xff];
const YELLOW: [u8; 4] = [0xff, 0xd4, 0x00, 0xff];
const YELLOW_D: [u8; 4] = [0xb3, 0x86, 0x00, 0xff];
const RED: [u8; 4] = [0xe6, 0x00, 0x12, 0xff];
const RED_D: [u8; 4] = [0x8f, 0x00, 0x0b, 0xff];
const HELMET: [u8; 4] = [0xff, 0xf8, 0xe6, 0xff];

/// The icon as RGBA, `size` x `size` (16..=256; drawn on a 20-cell grid).
pub fn rgba(size: u32) -> Vec<u8> {
    let s = size as usize;
    let mut px = vec![0u8; s * s * 4];
    let cell = s as f32 / 20.0;
    let mut put = |x: usize, y: usize, c: [u8; 4]| {
        if x < s && y < s {
            px[(y * s + x) * 4..][..4].copy_from_slice(&c);
        }
    };
    for y in 0..s {
        for x in 0..s {
            // Grid coordinates on the 20 x 20 design.
            let (gx, gy) = (x as f32 / cell, y as f32 / cell);
            let c = if gx < 1.0 || gy < 1.0 || gx >= 19.0 || gy >= 19.0 {
                INK
            } else if gy >= 17.0 {
                YELLOW_D
            } else {
                YELLOW
            };
            put(x, y, c);
        }
    }
    // The kart, 16 x 11 cells, centred (cells 2..18, 4..15).
    for y in 0..s {
        for x in 0..s {
            let gx = (x as f32 / cell) as i32 - 2;
            let gy = (y as f32 / cell) as i32 - 4;
            if !(0..16).contains(&gx) || !(0..11).contains(&gy) {
                continue;
            }
            let c = match KART[gy as usize].as_bytes()[gx as usize] {
                b'h' => HELMET,
                b'v' | b'w' => INK,
                b'b' => RED,
                b'd' => RED_D,
                _ => continue,
            };
            put(x, y, c);
        }
    }
    px
}

/// A minimal PNG (RGBA, stored deflate blocks): for the Windows toast icon.
#[cfg(any(windows, test))]
pub fn png(size: u32) -> Vec<u8> {
    encode_png(size, size, &rgba(size))
}

/// Any RGBA image as a PNG (also the window's MKWMP_GUI_SHOT screenshots).
pub fn encode_png(width: u32, height: u32, rgba: &[u8]) -> Vec<u8> {
    let mut raw = Vec::with_capacity(rgba.len() + height as usize);
    for row in rgba.chunks(width as usize * 4) {
        raw.push(0); // filter: none
        raw.extend_from_slice(row);
    }
    let mut z = vec![0x78, 0x01];
    let mut chunks = raw.chunks(65_535).peekable();
    while let Some(block) = chunks.next() {
        z.push(u8::from(chunks.peek().is_none()));
        let len = block.len() as u16;
        z.extend_from_slice(&len.to_le_bytes());
        z.extend_from_slice(&(!len).to_le_bytes());
        z.extend_from_slice(block);
    }
    z.extend_from_slice(&adler32(&raw).to_be_bytes());

    let mut out = b"\x89PNG\r\n\x1a\n".to_vec();
    let mut ihdr = Vec::new();
    ihdr.extend_from_slice(&width.to_be_bytes());
    ihdr.extend_from_slice(&height.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]); // 8-bit RGBA
    chunk(&mut out, b"IHDR", &ihdr);
    chunk(&mut out, b"IDAT", &z);
    chunk(&mut out, b"IEND", &[]);
    out
}

fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    let start = out.len();
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let crc = crc32(&out[start..]);
    out.extend_from_slice(&crc.to_be_bytes());
}

fn crc32(data: &[u8]) -> u32 {
    let mut c = !0u32;
    for &b in data {
        c ^= u32::from(b);
        for _ in 0..8 {
            c = if c & 1 != 0 { 0xedb8_8320 ^ (c >> 1) } else { c >> 1 };
        }
    }
    !c
}

fn adler32(data: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for &x in data {
        a = (a + u32::from(x)) % 65_521;
        b = (b + a) % 65_521;
    }
    (b << 16) | a
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn png_is_well_formed() {
        let p = png(64);
        assert_eq!(&p[..8], b"\x89PNG\r\n\x1a\n");
        assert_eq!(&p[12..16], b"IHDR");
        assert_eq!(crc32(b"IEND"), 0xae42_6082); // the standard IEND crc
        assert!(p.ends_with(&[0xae, 0x42, 0x60, 0x82]));
    }

    #[test]
    fn border_is_ink_and_kart_is_red() {
        let px = rgba(40);
        assert_eq!(&px[..4], &INK);
        // a body cell: kart row 6, column 8 -> grid (10, 10) -> pixel (21, 21) at 2 px/cell
        let i = (21 * 40 + 21) * 4;
        assert_eq!(&px[i..i + 4], &RED);
    }
}

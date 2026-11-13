"""Write and sign SHA256SUMS for a launcher release (rust/client/src/update.rs).

    python tools/release/sign_sums.py <version> <release key file> <asset>...

Writes SHA256SUMS ("# mkwmp <version>" then "<sha256>  <name>" per asset) and
SHA256SUMS.sig (Ed25519, base64url without padding, over SHA256SUMS) in the
current directory. The key file holds the base64url seed (`mkwmp keygen`);
keep it off the repo. Needs the `cryptography` package.
"""

import base64
import hashlib
import pathlib
import sys

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    version, key_file, assets = sys.argv[1], pathlib.Path(sys.argv[2]), sys.argv[3:]
    seed = key_file.read_text().strip()
    key = Ed25519PrivateKey.from_private_bytes(base64.urlsafe_b64decode(seed + "=" * (-len(seed) % 4)))
    lines = [f"# mkwmp {version}"]
    for a in assets:
        p = pathlib.Path(a)
        lines.append(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}")
    sums = ("\n".join(lines) + "\n").encode()
    pathlib.Path("SHA256SUMS").write_bytes(sums)
    sig = base64.urlsafe_b64encode(key.sign(sums)).decode().rstrip("=")
    pathlib.Path("SHA256SUMS.sig").write_text(sig + "\n")
    print(sums.decode(), end="")


if __name__ == "__main__":
    main()

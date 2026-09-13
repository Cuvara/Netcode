# Sealed Session Probe

Two peers in one process running ADR-22's transport crypto and ADR-25's server identity for real. **No server, no
network, no sockets.** Every frame the game writes is shown twice — the plaintext, and the
bytes a packet capture would have got — and eight buttons each mount an attack the design
says must fail.

## Why this scene exists

"The link is encrypted" is the kind of claim that gets believed rather than checked, and
stays believed after it stops being true. It has no visible symptom when it breaks: a
session that quietly sends cleartext looks exactly like one that does not, from inside the
game.

So this scene puts the two byte strings one above the other, updated live. The green line
is what the game wrote. The grey line beneath it is what anyone on the path would see.
Nothing here is a mock — it is the same `Cuvara.Netcode.Crypto` code the client will use,
running the same BouncyCastle the server runs.

## What it does

**The handshake**, once per session and again on *New session*:

- Two fresh ephemeral X25519 key pairs, one per peer
- The agreement, checked from both sides — they must reach the identical secret
- The transcript: `"cuvara/sealed-handshake/v1" ‖ 0x00 ‖ jti ‖ 0x00 ‖ clientPub ‖ serverPub`
- A binding over that transcript, HMAC-SHA256 under a key derived from the join-token
  secret — the proof of possession, because the token itself is readable on the wire and
  proves nothing
- HKDF-SHA256 to **two** one-direction keys, salted by the transcript

**The server's identity** (ADR-25), generated fresh with each session:

- An Ed25519 keypair, made here the way the real pod makes it — at startup, from an RNG,
  never persisted and never mounted, because a key that can be mounted eventually is
- A signature over `"cuvara/sealed-identity/v1" ‖ 0x00 ‖ transcript ‖ 0x00 ‖ identityPub`,
  built by calling `ServerIdentityVerifier.IdentityInput` rather than re-spelling the layout;
  a probe that assembled its own bytes would agree with itself while disagreeing with the
  server, which is the single failure it exists to catch
- The verdict from `ServerIdentityVerifier.Evaluate`, in its own words

**The `Gateway hop authenticated` toggle is the lesson of this whole section.** The signature
does not change when you flip it. What changes is the verdict, because the identity key
arrives on the *gateway* hop and a signature over a key an attacker supplied proves nothing.
With the toggle off you get "signature checked, but the key arrived over an UNAUTHENTICATED
hop" — which is the honest description of every deployment that has not turned on ADR-23's
TLS.

**The traffic**, at the configured cadence: a synthetic input frame is sealed with
ChaCha20-Poly1305, carried, and opened. The sequence is the nonce and the whole 10-byte
header is additional authenticated data.

## The buttons are the substance

Each reports what *actually happened*, not what was intended. A refusal that stops working
turns the verdict red.

| Button | The attack | Must be refused by |
|---|---|---|
| Flip one bit in flight | one bit of ciphertext altered in transit | the Poly1305 tag |
| Replay the last frame | a byte-perfect replay of an accepted frame | the sequence counter — **the tag on it is valid**, which is why a cipher alone is not enough |
| Replay the header, garbage body | a captured header at sequence +100000 | the tag, **and the counter must not move** |
| Send a cleartext Envelope | a plain Protobuf Envelope where a sealed frame is required | the `0xC1` marker — reported as `NotSealed`, a distinct answer |
| Peer with the wrong `JOIN_TOKEN_SECRET` | a binding computed from the wrong secret | the transcript binding |
| Man in the middle swaps a key | a substituted ephemeral with the real client's binding replayed | the binding covering *both* publics |
| Flip one byte of the signature | one byte of the Ed25519 identity signature altered | Ed25519 — there is no partially-correct signature |
| Substitute the identity key and re-sign | the attacker's own identity key, signed with the matching private half | **nothing, while the hop is plaintext** — see below |

The last one is the only button in this scene whose honest answer depends on a setting, and
it is here to make that dependence visible. The attacker's signature is genuine; there is
nothing wrong with it. What stops the attack is the key never reaching the client in the
first place, and that is gateway TLS, not cryptography on this hop. With the toggle off the
button reports ACCEPTED — **that is not a defect in this code**, it is the plaintext
deployment being reported truthfully. Turn the toggle on and it reports refused, because a
substituted key cannot be delivered over a hop that is authenticated.

The third one is worth watching closely. If the sequence were checked *before* the tag —
which is the natural-looking way to write it, since the sequence is right there in the
cleartext header — an attacker could push a peer's counter arbitrarily high without holding
any key, and lock out the real sender. `SealedSession.Open` owns the order so no call site
can get it wrong, and this button is what proves it still does.

## What this scene does NOT prove

That a real server behaves this way. Both peers here are this process, so the scene proves
the primitives, the refusals and the *reporting* on this runtime with these bytes — not that
the game server signs what it claims to sign. The cross-implementation agreement is pinned
elsewhere, by the interop vectors shared with the backend's own tests.

It also does not prove the ADR-22 binding protects a shipped client, because no shipped
client can verify it: the binding's key is `JOIN_TOKEN_SECRET`, and a client holding that
could mint tokens. The binding buttons here run with a synthetic secret both peers know. The
identity signature is the part that a real client genuinely can check, which is why it was
added.

## Running it

Import the sample, open `Scenes/SealedSessionProbe.unity`, press Play. Nothing external is
required — no backend, no secret, no network.

The `joinTokenSecret` and `jti` fields on the component are synthetic. This scene issues no
tokens and talks to nothing; changing them changes the derived keys and nothing else.

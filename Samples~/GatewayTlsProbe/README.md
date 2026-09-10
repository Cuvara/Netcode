# Gateway TLS probe

The client half of ADR-23, run for real against listeners the scene starts itself. No
gateway, no configuration, no network.

## The question

The gateway can terminate TLS on the client hop. Turning that on in the client is one
boolean — and a boolean is exactly the kind of thing that can be wrong in a way nothing
notices:

- it can produce a **plaintext** link while every log line still says TLS;
- it can produce a TLS link whose **validation accepts anything**, which is
  indistinguishable from validation that works if only a good certificate is ever tested;
- it can be **on in the client and off in the gateway**, or the reverse, and quietly fall
  back to whatever still connects.

So four of the five cases here are **refusals**. A probe that only showed a good
certificate connecting would pass on a client where TLS is worthless.

## What it runs

| case | expected |
|---|---|
| TLS + pinned certificate | **connects**, and a frame round-trips |
| TLS, no pin — platform trust store decides | **refused**: the certificate is self-signed |
| TLS client → plaintext gateway | **refused**: never a silent downgrade |
| plaintext client → TLS gateway | **no usable frame**: the mismatch is loud |
| factory asked for `TcpTls` with no `TlsOptions` | **throws**, rather than returning cleartext |

The last one is not a network case at all — it is the code path where "TLS requested" could
most cheaply become "plaintext returned", so it is asserted next to the others.

The certificate is embedded — the same self-signed `CN=localhost` the server repo's IL2CPP
probe carries — rather than generated at runtime. Whether `CertificateRequest` works under
IL2CPP is a *separate* question, and a failure there would look like a TLS failure while
being nothing of the kind.

The pin is that certificate's own public leaf (`X509Certificate2.RawData`), so the pinned
case cannot pass by accident: if the listener ever presented a different certificate, the
bytes would not match.

## Running it

Import the sample, open `Scenes/GatewayTlsProbe.unity`, press Play, then **Run every case**.
Nothing external is needed and nothing leaves the machine — both listeners are on
`127.0.0.1` on ports the OS assigns.

The verdict line at the bottom counts surprises. Green with a non-zero count of cases is
the expected state; red means something behaved differently from what the design says, and
the log names which case and what happened.

## What this does NOT prove

- **That your gateway is configured for TLS.** That is `GATEWAY_TLS_CERT` /
  `GATEWAY_TLS_KEY` on the server, which this scene never contacts.
- **That the certificate you ship with is trusted where the game runs.** The unpinned case
  here is *expected to fail*, because the test certificate is self-signed. Against a real
  gateway with a publicly-trusted certificate the same case is expected to succeed — and if
  it does not, that is a finding about the device's trust store.
- **Anything about Android.** The server repo's probe measured a Windows IL2CPP player at
  both Minimal and High stripping; Android is a separate answer, unmeasured.

## Using it in a game

```csharp
var settings = new NetworkSettings
{
    GatewayHost = "gateway.example.com",
    GatewayPort = 8000,
    GatewayUseTls = true,
    // Omit the pin against a publicly-trusted certificate — the platform trust store is
    // the stronger default. Set it only to reach a gateway holding a self-signed one:
    GatewayTlsPinnedCertificate = TlsOptions.FromPem(File.ReadAllText(devCertPath)),
};
```

There is deliberately **no** "TLS on, validation off" option. A developer who needs to
reach a self-signed dev gateway pins its certificate, which is stricter than the public
trust store rather than looser. If that option ever appears in this package, this scene's
second case is what should have stopped it.

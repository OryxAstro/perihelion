# Security Policy

## Supported versions

Perihelion doesn't have tagged releases yet — it's tracked as a working prototype on `main`.
Until a versioned release process exists, only the latest commit on `main` is supported;
please make sure you're running the current build before reporting an issue.

## Scope

Perihelion runs in-process inside PINS/N.I.N.A. and can command a connected mount's tracking
rate and a guider's shift rate. A security issue here isn't purely theoretical — in the worst
case it could mean unexpected or incorrect commands reaching real hardware. Please treat
anything in that category (not just the usual web/API concerns around the plugin's own HTTP
server) as in scope and worth reporting privately rather than as a public issue.

## Reporting a vulnerability

Please **do not** open a public GitHub issue for a security concern.

Use GitHub's private vulnerability reporting for this repository instead:
[Report a vulnerability](https://github.com/OryxAstro/perihelion/security/advisories/new)
(under the repo's **Security** tab → **Advisories** → **Report a vulnerability**).

If that's ever unavailable, open a regular issue asking for a private contact channel rather
than describing the vulnerability itself.

What's helpful in a report:

- The affected component (Perihelion's own C# plugin, its standalone HTTP API, or the
  Touch-N-Stars panel) and, if known, the specific file or route.
- Steps to reproduce, and what you'd expect to happen instead.
- Whether it requires physical/network access to the PINS box, or is reachable from the
  Touch-N-Stars panel alone.

## Dependencies

Dependency updates are tracked via Renovate. A Renovate PR touching `CosineKitty.AstronomyEngine`,
`EmbedIO`, or `Unosquare.Swan.Lite` needs a manual compatibility check before merging — see
`CLAUDE.md`'s own notes on why those three are pinned rather than auto-bumped.

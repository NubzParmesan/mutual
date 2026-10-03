# Security

Mutual controls PCs, so security problems matter more here than in most projects.

## Reporting a hole

Please don't open a public issue for it. Use **[Report a vulnerability](https://github.com/NubzParmesan/mutual/security/advisories/new)** on the Security tab instead. Only I can see those reports, which gives me time to fix it before everyone knows.

Helpful things to include:

- what an attacker could do with it, and from where (the paired friend, someone on the same network, the rendezvous server, another program on the PC)
- how to make it happen
- the Mutual version (Settings, or the file properties of Mutual.exe)

I'll reply as soon as I can, and once it's fixed I'm happy to credit you in the release notes if you want.

## What counts

Anything that gets past the consent model or the pairing is in scope. Some examples: connecting, streaming or controlling without the other person accepting, getting in without the paired key, the rendezvous server reading or faking anything, input reaching outside what's shared, or anything that ends with code running as admin.

Some things are how Mutual works and aren't holes:

- a friend you paired with and gave control can use whatever you're sharing. That's the point, and Ctrl+Alt+End takes it back
- a program already running as you on your own PC can do what you can do
- the rendezvous server can see when the two of you are online

## Supported versions

Only the latest release gets fixes, so grab new versions from [Releases](https://github.com/NubzParmesan/mutual/releases).

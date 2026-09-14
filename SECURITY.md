# Security Policy

## Supported versions

Git for Unity is in alpha. Security fixes go into the latest release only.

| Version | Supported |
|---|---|
| 1.0.0-alpha.x | yes |

## Reporting a vulnerability

Please **do not** open a public issue. Report privately through
[GitHub Security Advisories](https://github.com/LatyninEugene/git-for-unity/security/advisories/new).

Include what an attacker can do, the steps to reproduce and the package and Unity versions.
You will get an answer within a week.

## How the package handles secrets

- Git credentials for push and pull are stored by `git credential` in the system credential helper; the package does not keep them.
- The GitLab API token is stored in the system credential store (Windows Credential Manager, Secret Service on Linux) — never in the project or in logs.
- Output of commands that handle credentials is not written to the command log.
- The problem report and the package log remove tokens, passwords and credentials in URLs.
- Connecting to a GitLab server over plain HTTP is allowed for local networks. The token then travels unencrypted — use HTTPS whenever possible.

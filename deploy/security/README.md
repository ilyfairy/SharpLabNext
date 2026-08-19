# Runtime sandbox policy

`runtime-job-seccomp.v1.json` is copied into the Runtime Supervisor image from
`src/Supervisor/SharpLabNext.RuntimeSupervisor/security`. It is the default
deny-by-default profile selected by Moby `v28.5.2`, commit
`89c5e8fd66634b6128fc4c0e6f1236e2540e46e0`. That release pins
`github.com/moby/profiles/seccomp` `v0.1.0`, commit
`c936cc7b4074219137bc0bee45670f5e4618d462`. The vendored JSON has SHA-256
`01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28`.
Supervisor verifies that digest before accepting runtime jobs and sends the
profile JSON directly to the Docker Engine API. `inventory.json` records the
complete source identity; `THIRD-PARTY-NOTICES.md` and `licenses/` carry the
redistribution notice and complete Apache-2.0 license text.

`sharplabnext-runtime-job-v1.apparmor` is the matching host policy for hardened
Linux deployments. Install it with `apparmor_parser` and set
`SHARPLABNEXT_RUNTIME_APPARMOR_PROFILE=sharplabnext-runtime-job-v1`. Docker
Desktop validation leaves AppArmor unset because its Linux VM does not expose
host AppArmor policy management.

The profile permits only local Unix-domain socket IPC and explicitly denies
IPv4 and IPv6 sockets. Unix-domain sockets are required by Wine's `wineserver`
and by managed runtime internals; a blanket `deny network` rule also blocks
that local IPC and makes otherwise valid .NET Framework programs fail before
their entry point. Docker `network=none` remains the primary network namespace
boundary, in addition to the AppArmor IPv4/IPv6 denial.

The offline bundle retains this complete directory. Install the profile from
the retained release directory before setting the environment variable; the
name configures AppArmor on dynamically created runtime and materializer
containers, not on the Runtime Supervisor container itself.

Both runtime jobs and workspace materializers also use `no-new-privileges`, no
network, all capabilities dropped, read-only root filesystems, private IPC,
PIDs/CPU/memory limits, `nofile=256`, and `core=0`.

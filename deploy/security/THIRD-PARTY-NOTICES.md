# Runtime Sandbox Third-Party Notices

The vendored `runtime-job-seccomp.v1.json` is the default seccomp profile
selected by Moby v28.5.2. Moby commit
`89c5e8fd66634b6128fc4c0e6f1236e2540e46e0` pins
`github.com/moby/profiles/seccomp` v0.1.0 at commit
`c936cc7b4074219137bc0bee45670f5e4618d462`.

The profile is licensed under Apache-2.0. The complete license text is in
`licenses/moby-profiles-Apache-2.0.txt`. The upstream `moby/profiles` repository
does not contain a separate `NOTICE` file at the pinned commit.

The profile is redistributed without source changes under the local filename
`src/Supervisor/SharpLabNext.RuntimeSupervisor/security/runtime-job-seccomp.v1.json`.
Its SHA-256 is
`01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28`.

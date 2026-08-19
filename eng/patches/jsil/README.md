# JSIL patches

SharpLabNext builds the original `sq/JSIL` source at commit
`1d57d5427c87ab92ffa3ca4b82429cd7509796ba`.

`0001-fix-mono-linux-path-handling.patch` fixes two CLI-only assumptions that
prevent that commit from translating any assembly on Mono/Linux:

- filesystem paths are converted to explicit `file://` URIs before computing
  relative display paths;
- adjacent `.jsilconfig` discovery stops at the filesystem root instead of
  calling `IndexOfAny` with an out-of-range starting index.

That patch does not change IL translation, JavaScript emission, proxies, or
runtime semantics.

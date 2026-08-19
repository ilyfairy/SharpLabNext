/**
 * Validate the Catalog channel relationship for generated runtime profiles.
 *
 * Schema validation is deliberately handled by validate-schemas.mjs. This
 * helper only enforces the deployment-channel rule: top-level profiles are
 * active inputs and must close against a selectable Catalog runtime, while
 * profiles under candidates/ are review-only and may be new or supersede an
 * active logical ID until promotion.
 */
export function validateRuntimeProfileChannels(runtimePaths, catalog, readProfile) {
  const failures = []
  const runtimes = new Map((catalog.runtimes ?? []).map(runtime => [runtime.id, runtime]))

  for (const relativePath of runtimePaths) {
    let profile
    try {
      profile = readProfile(relativePath)
    } catch (error) {
      failures.push(`${relativePath}: cannot validate Catalog channel (${error.message})`)
      continue
    }

    // Candidate profiles are schema-checked by the caller, then closed against
    // the release lock and immutable image during promotion. Do not compare
    // them with the currently active Catalog here: a candidate may be absent
    // from the Catalog or carry a newer version under the same logical ID.
    if (relativePath.startsWith('profiles/runtimes/candidates/')) continue

    const runtime = runtimes.get(profile.id)
    if (runtime === undefined) {
      failures.push(`${relativePath}: runtime profile ID '${profile.id}' is absent from the Catalog`)
      continue
    }

    const isSelectable = runtime.availability?.installed === true && runtime.availability?.health === 'healthy'
    if (!isSelectable) {
      failures.push(`${relativePath}: active profile '${profile.id}' maps to a non-selectable Catalog runtime`)
      continue
    }
    if (typeof runtime.resolvedVersion === 'string' && runtime.resolvedVersion !== profile.runtimeVersion) {
      failures.push(`${relativePath}: runtimeVersion '${profile.runtimeVersion}' does not match Catalog '${runtime.resolvedVersion}'`)
    }
    if (typeof runtime.runtimeImageId === 'string' && runtime.runtimeImageId !== profile.runtimeImageId) {
      failures.push(`${relativePath}: runtimeImageId does not match the selectable Catalog identity`)
    }
  }

  return failures
}

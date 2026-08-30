export type BuildConfiguration = 'debug' | 'release'
export type BuildOutputKind = 'auto' | 'console' | 'library' | 'windows-application'
export type NullableContextMode = 'project-default' | 'disable' | 'enable' | 'warnings' | 'annotations'

export interface ComponentAvailability {
  installed: boolean
  health: string
  reason?: string
}

export interface LanguageManifest {
  id: string
  displayName: string
  monacoLanguageId: string
  extensions: string[]
  defaultFileName: string
  defaultSource: string
  defaultToolchainId: string
  capabilities: string[]
  legacyAliases: string[]
}

export interface ToolchainManifest {
  id: string
  displayName: string
  workerId: string
  releaseTrack: string
  resolvedVersion: string
  defaultReferenceSetId: string
  supportedLanguageIds: string[]
  allowedReferenceSetIds: string[]
  producesArtifactFormats: string[]
  capabilities: string[]
  metadataFeatureTags: string[]
  legacyAliases: string[]
  availability: ComponentAvailability
}

export interface ReferenceSetManifest {
  id: string
  displayName: string
  targetFramework: string
  digest: string
  runtimeFamily: string
  requiredRuntimeFeatureTags: string[]
  metadataFeatureTags: string[]
  supportStatus?: 'active' | 'maintenance' | 'preview' | 'legacy' | 'experimental'
  supportEndDate?: string
  visibility?: 'visible' | 'hidden'
  replacementReferenceSetId?: string
  availability: ComponentAvailability
}

export interface RuntimeManifest {
  id: string
  displayName: string
  family: string
  resolvedVersion: string
  rid: string
  architecture: string
  acceptedArtifactFormats: string[]
  capabilities: string[]
  runtimeCommit?: string
  jitVersion?: string
  jitCommit?: string
  runtimeImageId?: string
  acceptedRuntimeFamilies?: string[]
  acceptedFrameworks?: RuntimeFrameworkManifest[]
  containerIsolationKind?: string
  containerEnvironmentKind?: string
  jitSourceMappingKind?: string
  providedRuntimeFeatureTags: string[]
  providedMetadataFeatureTags: string[]
  legacyAliases: string[]
  supportStatus?: 'active' | 'maintenance' | 'preview' | 'legacy' | 'experimental'
  supportEndDate?: string
  visibility?: 'visible' | 'hidden'
  availability: ComponentAvailability
}

export interface RuntimeFrameworkManifest {
  name: string
  minimumVersion?: string
  maximumVersion?: string
  exactVersion?: string
}

export interface ArtifactProcessorManifest {
  id: string
  displayName: string
  resolvedVersion: string
  workerId: string
  acceptsArtifactFormats: string[]
  producesArtifactFormats: string[]
  capabilities: string[]
  transformations?: ArtifactTransformationManifest[]
  acceptedMetadataFeatureTags: string[]
  availability: ComponentAvailability
}

export interface ArtifactTransformationManifest {
  id: string
  inputArtifactFormat: string
  outputArtifactFormat: string
}

export interface OutputManifest {
  id: string
  displayName: string
  renderer: string
  requiresRuntime: boolean
  requiredCapabilities: string[]
  acceptedArtifactFormats: string[]
  outputArtifactFormat?: string
}

export type CompatibilityRuleKind = 'toolchain-reference-set' | 'artifact-processor' | 'artifact-runtime'

export interface CompatibilityRule {
  id: string
  kind: CompatibilityRuleKind
  fromId: string
  toId: string
  allowed: boolean
  reason?: string
  requiredMetadataFeatureTags: string[]
}

export interface ProfilePreset {
  id: string
  displayName: string
  languageId: string
  toolchainId: string
  referenceSetId: string
  defaultOutputId: string
  defaultRuntimeId?: string
  legacyAliases: string[]
  supportStatus?: 'active' | 'maintenance' | 'preview' | 'legacy' | 'experimental'
  supportEndDate?: string
  visibility?: 'visible' | 'hidden'
  availability: ComponentAvailability
}

export interface CatalogDocument {
  schemaVersion: number
  revision: string
  releaseId: string
  languages: LanguageManifest[]
  toolchains: ToolchainManifest[]
  referenceSets: ReferenceSetManifest[]
  runtimes: RuntimeManifest[]
  artifactProcessors: ArtifactProcessorManifest[]
  outputs: OutputManifest[]
  compatibility: CompatibilityRule[]
  presets: ProfilePreset[]
}

export interface ResolveSelectionRequest {
  languageId: string
  toolchainId: string | null
  referenceSetId: string | null
  outputId: string
  runtimeId: string | null
  buildMode: BuildConfiguration
  catalogRevision: string
  workspaceRevision: number
}

export interface ResolvedSelection {
  languageId: string
  toolchainId: string
  referenceSetId: string
  outputId: string
  runtimeId?: string | null
}

export type SelectionField = 'language' | 'toolchain' | 'reference-set' | 'output' | 'runtime' | 'build-mode'

export type SelectionChangeReason = 'default-applied' | 'legacy-alias-resolved' | 'unsupported-by-language' | 'incompatible-reference-set' | 'incompatible-artifact' | 'runtime-not-required' | 'profile-unavailable'

export interface SelectionChange {
  field: SelectionField
  requestedValue?: string | null
  effectiveValue?: string | null
  reason: SelectionChangeReason
  message: string
}

export interface EffectiveCapabilities {
  languageServerCapabilities: string[]
  buildCapabilities: string[]
  outputCapabilities: string[]
  runtimeCapabilities: string[]
}

export type PipelineStageKind = 'build' | 'transform' | 'render' | 'verify' | 'run' | 'jit' | 'explain'

export interface PipelineStageDescriptor {
  id: string
  kind: PipelineStageKind
  providerId: string
  inputArtifactFormat?: string | null
  outputArtifactFormat?: string | null
}

export interface PipelinePlanDescriptor {
  releaseId: string
  languageWorkerId: string
  compilerWorkerId: string
  referenceSetId: string
  stages: PipelineStageDescriptor[]
  runtimeId?: string | null
  securityPolicyId: string
  workerImageIds: string[]
}

export interface ResolveSelectionResponse {
  effectiveSelection: ResolvedSelection
  selectionChanges: SelectionChange[]
  effectiveCapabilities: EffectiveCapabilities
  pipelineResolutionId: string
  pipelinePlan: PipelinePlanDescriptor
  expiresAt: string
}

export interface BuildOptions {
  configuration: BuildConfiguration
  optimize: boolean
  outputKind: BuildOutputKind
  allowUnsafe?: boolean
  emitPortablePdb?: boolean
  nullableContext?: NullableContextMode
  languageVersion?: string
  preprocessorSymbols?: string[]
  checkOverflow?: boolean
}

export interface WorkspaceFile {
  path: string
  version: number
  text: string
}

export interface WorkspaceSnapshot {
  schemaVersion: number
  revision: number
  selectionRevision: number
  languageId: string
  files: WorkspaceFile[]
  activeFile: string
  sourceOrder: string[]
  referenceSetId: string
  buildOptions: BuildOptions
}

export interface OpenLanguageSessionRequest {
  requestId: string
  pipelineResolutionId: string
  languageId: string
  toolchainId: string
  referenceSetId: string
  workspace: WorkspaceSnapshot
  lspVersion: '3.17'
}

export interface GatewayLanguageSession {
  sessionId: string
  languageId: string
  toolchainId: string
  compilerBuildIdentity: string
  lspVersion: '3.17'
  workspaceRevision: number
  selectionRevision: number
  expiresAtUtc: string
  webSocketUrl: string
  capabilities: string[]
}

export type BuildTarget = 'artifact' | 'compile-check' | 'ast' | 'generated-source'

export interface BuildRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  toolchainId: string
  referenceSetId: string
  workspace: WorkspaceSnapshot
  deadlineUtc: string
  options?: BuildOptions
  target: BuildTarget
}

export interface ExplainRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  workspace: WorkspaceSnapshot
  deadlineUtc: string
}

export type ArtifactRef = string
export type ContentRef = string

export interface RenderArtifactOptions {
  includeSequencePoints: boolean
  includeCompilerGeneratedMembers: boolean
  maxCharacters: number
}

export interface RenderArtifactRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  artifactRef: ArtifactRef
  processorId: string
  outputId: string
  options: RenderArtifactOptions
  deadlineUtc: string
}

export interface TransformArtifactOptions {
  preservePortablePdb: boolean
  preserveSequencePoints: boolean
  rewriterProfileId?: string | null
}

export interface TransformArtifactRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  artifactRef: ArtifactRef
  processorId: string
  transformId: string
  options: TransformArtifactOptions
  deadlineUtc: string
}

export interface VerifyArtifactOptions {
  verificationProfileId: string
  includeMetadataTokens: boolean
  maxFindings: number
}

export interface VerifyArtifactRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  artifactRef: ArtifactRef
  processorId: string
  options: VerifyArtifactOptions
  deadlineUtc: string
}

export type RunInstrumentation = 'none' | 'inspection' | 'execution-flow'

export interface RunOptions {
  arguments: string[]
  stdin?: string | null
  instrumentation: RunInstrumentation
  securityPolicyId: string
}

export interface RunRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  artifactRef: ArtifactRef
  runtimeProfileId: string
  options: RunOptions
  deadlineUtc: string
}

export interface JitOptions {
  methodFilter?: string | null
  tieringPolicyId: string
  pgoPolicyId: string
  providerId: string
  securityPolicyId: string
}

export interface JitRequest {
  requestId: string
  idempotencyKey: string
  pipelineResolutionId: string
  artifactRef: ArtifactRef
  runtimeProfileId: string
  options: JitOptions
  deadlineUtc: string
}

export interface OperationHandle {
  operationId: string
  requestId: string
  createdAtUtc: string
  isExisting: boolean
}

export type OperationKind = 'build' | 'transform-artifact' | 'render-artifact' | 'verify-artifact' | 'run' | 'jit' | 'explain'

export type OperationStatus = 'accepted' | 'running' | 'cancelling' | 'completed' | 'failed' | 'cancelled'

export type WorkerErrorCategory = 'invalid-argument' | 'not-found' | 'unsupported-capability' | 'stale-revision' | 'incompatible-artifact' | 'resource-exhausted' | 'deadline-exceeded' | 'cancelled' | 'unavailable' | 'internal'

export interface WorkerError {
  code: string
  category: WorkerErrorCategory
  publicMessage: string
  retryable: boolean
  safeToRetry: boolean
  traceId: string
  workerId: string
  workerImageId: string
}

export interface OperationState {
  operationId: string
  requestId: string
  kind: OperationKind
  status: OperationStatus
  lastSequence: number
  createdAtUtc: string
  updatedAtUtc: string
  completedAtUtc?: string | null
  traceId: string
  error?: WorkerError | null
}

export interface TextRange {
  startLine: number
  startCharacter: number
  endLine: number
  endCharacter: number
}

export interface Diagnostic {
  source: string
  code: string
  severity: 'hidden' | 'information' | 'warning' | 'error'
  message: string
  filePath?: string | null
  range?: TextRange | null
  relatedInformation: unknown[]
  tags: string[]
  workspaceRevision: number
  selectionRevision: number
}

export interface AstNode {
  kind: string
  range: TextRange
  fullRange?: TextRange | null
  properties: Record<string, string | null>
  children: AstNode[]
}

export interface AstDocument {
  languageId: string
  toolchainId: string
  workspaceRevision: number
  root: AstNode
  truncated: boolean
}

export interface BuildResult {
  resultType: 'build'
  outcome: 'succeeded' | 'compilation-failed' | 'emit-failed'
  artifactRef?: string | null
  diagnostics: Diagnostic[]
  identity: BuildIdentity
  workspaceRevision: number
  selectionRevision: number
}

export interface CompilationCheckResult {
  resultType: 'compile-check'
  compilationSucceeded: boolean
  diagnostics: Diagnostic[]
  identity: BuildIdentity
  workspaceRevision: number
  selectionRevision: number
}

export interface AstResult {
  resultType: 'ast'
  document: AstDocument
  identity?: BuildIdentity | null
}

export interface ExplanationNode {
  kind: string
  title: string
  description: string
  range: TextRange
  depth: number
}

export interface ExplanationFile {
  path: string
  nodes: ExplanationNode[]
}

export interface ExplanationDocument {
  languageId: string
  toolchainId: string
  workspaceRevision: number
  selectionRevision: number
  files: ExplanationFile[]
  truncated: boolean
}

export interface ExplainResult {
  resultType: 'explain'
  document: ExplanationDocument
  identity?: BuildIdentity | null
}

export interface GeneratedSourceResult {
  resultType: 'generated-source'
  documents: GeneratedSourceDocument[]
  identity: BuildIdentity
  workspaceRevision: number
  selectionRevision: number
}

export interface GeneratedSourceDocument {
  path: string
  contentRef: ContentRef
  languageId: string
  generatorId: string
}

export interface BuildIdentity {
  releaseId: string
  languageId: string
  toolchainId: string
  compilerVersion: string
  compilerCommit?: string | null
  referenceSetId: string
  workerImageId: string
}

export interface ArtifactProcessorIdentity {
  releaseId: string
  processorId: string
  processorVersion: string
  workerImageId: string
}

export type ArtifactJobOutcome = 'succeeded' | 'unsupported-artifact' | 'invalid-artifact' | 'limit-exceeded'

export interface LinkedRange {
  sourceFilePath?: string | null
  sourceRange?: TextRange | null
  outputRange: TextRange
  precision?: 'sequence-point' | 'method' | null
}

export interface ArtifactRenderResult {
  resultType: 'artifact-render'
  outcome: ArtifactJobOutcome
  contentRef?: ContentRef | null
  mediaType: string
  linkedRanges: LinkedRange[]
  diagnostics: Diagnostic[]
  identity?: ArtifactProcessorIdentity | null
}

export type ArtifactVerificationOutcome = 'valid' | 'findings' | 'unsupported-artifact' | 'invalid-artifact' | 'limit-exceeded'

export interface VerificationFinding {
  code: string
  message: string
  typeName?: string | null
  methodName?: string | null
  metadataToken?: number | null
  filePath?: string | null
  range?: TextRange | null
}

export interface ArtifactVerificationResult {
  resultType: 'artifact-verification'
  outcome: ArtifactVerificationOutcome
  findings: VerificationFinding[]
  verifierId: string
  verifierVersion: string
  identity?: ArtifactProcessorIdentity | null
}

export interface UserExceptionInfo {
  typeName: string
  message: string
  stackTrace?: string | null
  innerException?: UserExceptionInfo | null
}

export interface RuntimeIdentity {
  runtimeVersion: string
  runtimeCommit: string
  runtimeImageId: string
  rid: string
  architecture: string
}

export type RunTerminalStatus = 'completed' | 'user-exception' | 'non-zero-exit' | 'timeout' | 'out-of-memory' | 'process-crash' | 'cancelled' | 'output-limit-exceeded'

export interface RunResult {
  resultType: 'run'
  status: RunTerminalStatus
  exitCode?: number | null
  exception?: UserExceptionInfo | null
  elapsed: string
  outputTruncated: boolean
  identity: RuntimeIdentity
}

export interface JitMethodSummary {
  methodId: string
  displayName: string
  nativeCodeSize: number
  instructionCount: number
  linkedRanges: LinkedRange[]
}

export interface JitIdentity extends RuntimeIdentity {
  jitVersion: string
  jitCommit: string
  cpuFeatureProfile: string
  tieringPolicy: string
  pgoPolicy: string
  jitProvider: string
  inspectionMethod: string
}

export type JitTerminalStatus = 'completed' | 'no-matching-methods' | 'inspection-failed' | 'timeout' | 'out-of-memory' | 'process-crash' | 'cancelled' | 'output-limit-exceeded'

export interface JitResult {
  resultType: 'jit'
  status: JitTerminalStatus
  structuredDocumentRef?: ContentRef | null
  rawTextRef?: ContentRef | null
  methods: JitMethodSummary[]
  elapsed: string
  identity: JitIdentity
}

export interface ArtifactTransformResult {
  resultType: 'artifact-transform'
  outcome: ArtifactJobOutcome
  artifactRef?: ArtifactRef | null
  sourceArtifactRef: ArtifactRef
  artifactFormat?: string | null
  diagnostics: Diagnostic[]
  identity?: ArtifactProcessorIdentity | null
}

export type OperationResult = BuildResult | CompilationCheckResult | AstResult | GeneratedSourceResult | ArtifactTransformResult | ArtifactRenderResult | ArtifactVerificationResult | RunResult | JitResult | ExplainResult

export type OutputChannel = 'stdout' | 'stderr' | 'inspection' | 'flow' | 'jit' | 'log'

export interface OutputChunk {
  channel: OutputChannel
  encoding: 'utf-8' | 'binary'
  data: string
  truncated: boolean
}

export type OperationEventPayload =
  | { kind: 'accepted'; requestId: string; operationKind: OperationKind }
  | {
      kind: 'progress'
      stage: string
      message?: string | null
      fraction?: number | null
    }
  | { kind: 'diagnostic'; diagnostic: Diagnostic }
  | { kind: 'output-chunk'; chunk: OutputChunk }
  | {
      kind: 'artifact-produced'
      artifactRef: string
      artifactFormat: string
      role: string
    }
  | {
      kind: 'content-produced'
      contentRef: string
      mediaType: string
      size: number
    }
  | { kind: 'typed-result'; result: OperationResult }
  | {
      kind: 'output-truncated'
      channel: OutputChannel
      reason: string
      observedBytes: number
      limitBytes: number
    }
  | { kind: 'completed'; status: 'completed' | 'cancelled'; elapsed: string }
  | { kind: 'failed'; error: WorkerError }

export interface OperationEvent {
  operationId: string
  sequence: number
  timestampUtc: string
  traceId: string
  payload: OperationEventPayload
}

export type CancelDisposition = 'accepted' | 'already-cancelling' | 'already-terminal' | 'not-found'

export interface CancelResult {
  operationId: string
  disposition: CancelDisposition
  lastSequence: number
}

export interface ApiProblem {
  type?: string
  title?: string
  status?: number
  detail?: string
  error?: string
  code?: string
  field?: string
  value?: string | null
  message?: string
  traceId?: string
}

export interface GitHubAuthStatus {
  available: boolean
  authenticated: boolean
  login?: string | null
  csrfToken?: string | null
}

export interface GitHubOAuthStartResponse {
  authorizationUrl: string
}

export interface GistSourceFile {
  path: string
  text: string
}

export interface GistWorkspaceState {
  schemaVersion: number
  languageId: string
  toolchainId?: string | null
  referenceSetId?: string | null
  outputId: string
  runtimeId?: string | null
  buildMode: BuildConfiguration
  releaseId?: string | null
  activeFile: string
  sourceOrder: string[]
  files: GistSourceFile[]
  legacyBranchId?: string | null
}

export interface CreateGistRequest {
  description: string
  isPublic: boolean
  workspace: GistWorkspaceState
}

export interface UpdateGistRequest {
  description: string
  workspace: GistWorkspaceState
}

export interface GistDocument {
  id: string
  htmlUrl: string
  ownerLogin?: string | null
  isPublic: boolean
  canUpdate: boolean
  description: string
  sourceFormat: 'sharplabnext-v1' | 'sharplab-v1' | 'github-gist' | string
  workspace: GistWorkspaceState
  warnings: string[]
  updatedAtUtc?: string | null
}

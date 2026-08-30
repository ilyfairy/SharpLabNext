import type {
  ApiProblem,
  ArtifactProcessorIdentity,
  ArtifactProcessorManifest,
  ArtifactRenderResult,
  ArtifactTransformResult,
  ArtifactVerificationResult,
  AstDocument,
  AstNode,
  BuildIdentity,
  BuildOptions,
  BuildRequest,
  BuildResult,
  CancelResult,
  CatalogDocument,
  CompilationCheckResult,
  CreateGistRequest,
  Diagnostic,
  ExplainRequest,
  ExplainResult,
  GatewayLanguageSession,
  GeneratedSourceDocument,
  GeneratedSourceResult,
  GistDocument,
  GistSourceFile,
  GistWorkspaceState,
  GitHubAuthStatus,
  GitHubOAuthStartResponse,
  JitIdentity,
  JitMethodSummary,
  JitRequest,
  JitResult,
  LanguageManifest,
  LinkedRange,
  OpenLanguageSessionRequest,
  OperationEvent,
  OperationHandle,
  OperationState,
  OutputChunk,
  PipelinePlanDescriptor,
  PipelineStageDescriptor,
  ReferenceSetManifest,
  RenderArtifactRequest,
  ResolveSelectionRequest,
  ResolveSelectionResponse,
  RunRequest,
  RuntimeIdentity,
  SelectionChange,
  ToolchainManifest,
  TransformArtifactRequest,
  UpdateGistRequest,
  UserExceptionInfo,
  VerifyArtifactRequest,
  WorkerError,
  WorkspaceFile,
  WorkspaceSnapshot,
} from './types'

/**
 * Type-level view of the public SharpLabNext JSON contract.
 *
 * `types.ts` is the browser's internal model and intentionally uses the
 * idiomatic camelCase spelling. The objects crossing a SharpLabNext-owned
 * HTTP/operation-WebSocket boundary are the corresponding `Wire*` types and
 * use PascalCase. `encodeWire`/`decodeWire` are the runtime adapters for the
 * same boundary. External LSP, Docker, and GitHub payloads are not included.
 */
export type PascalCaseKey<K extends PropertyKey> = K extends string ? (string extends K ? string : K extends `${infer First}${infer Rest}` ? `${Uppercase<First>}${Rest}` : K) : K;

export type PascalCaseWire<T> = T extends readonly (infer Item)[] ? PascalCaseWire<Item>[] : T extends object ? (T extends (...args: never[]) => unknown ? T : { [K in keyof T as PascalCaseKey<K>]: PascalCaseWire<T[K]> }) : T;

export type WireApiProblem = PascalCaseWire<ApiProblem>
export type WireArtifactProcessorIdentity = PascalCaseWire<ArtifactProcessorIdentity>
export type WireArtifactProcessorManifest = PascalCaseWire<ArtifactProcessorManifest>
export type WireArtifactRenderResult = PascalCaseWire<ArtifactRenderResult>
export type WireArtifactTransformResult = PascalCaseWire<ArtifactTransformResult>
export type WireArtifactVerificationResult = PascalCaseWire<ArtifactVerificationResult>
export type WireAstDocument = PascalCaseWire<AstDocument>
export type WireAstNode = PascalCaseWire<AstNode>
export type WireBuildIdentity = PascalCaseWire<BuildIdentity>
export type WireBuildOptions = PascalCaseWire<BuildOptions>
export type WireBuildRequest = PascalCaseWire<BuildRequest>
export type WireBuildResult = PascalCaseWire<BuildResult>
export type WireCancelResult = PascalCaseWire<CancelResult>
export type WireCatalogDocument = PascalCaseWire<CatalogDocument>
export type WireCompilationCheckResult = PascalCaseWire<CompilationCheckResult>
export type WireCreateGistRequest = PascalCaseWire<CreateGistRequest>
export type WireDiagnostic = PascalCaseWire<Diagnostic>
export type WireExplainRequest = PascalCaseWire<ExplainRequest>
export type WireExplainResult = PascalCaseWire<ExplainResult>
export type WireGeneratedSourceDocument = PascalCaseWire<GeneratedSourceDocument>
export type WireGeneratedSourceResult = PascalCaseWire<GeneratedSourceResult>
export type WireGatewayLanguageSession = PascalCaseWire<GatewayLanguageSession>
export type WireGistDocument = PascalCaseWire<GistDocument>
export type WireGistSourceFile = PascalCaseWire<GistSourceFile>
export type WireGistWorkspaceState = PascalCaseWire<GistWorkspaceState>
export type WireGitHubAuthStatus = PascalCaseWire<GitHubAuthStatus>
export type WireGitHubOAuthStartResponse = PascalCaseWire<GitHubOAuthStartResponse>
export type WireJitIdentity = PascalCaseWire<JitIdentity>
export type WireJitMethodSummary = PascalCaseWire<JitMethodSummary>
export type WireJitRequest = PascalCaseWire<JitRequest>
export type WireJitResult = PascalCaseWire<JitResult>
export type WireLanguageManifest = PascalCaseWire<LanguageManifest>
export type WireLinkedRange = PascalCaseWire<LinkedRange>
export type WireOpenLanguageSessionRequest = PascalCaseWire<OpenLanguageSessionRequest>
export type WireOperationEvent = PascalCaseWire<OperationEvent>
export type WireOperationHandle = PascalCaseWire<OperationHandle>
export type WireOperationState = PascalCaseWire<OperationState>
export type WireOutputChunk = PascalCaseWire<OutputChunk>
export type WirePipelinePlanDescriptor = PascalCaseWire<PipelinePlanDescriptor>
export type WirePipelineStageDescriptor = PascalCaseWire<PipelineStageDescriptor>
export type WireReferenceSetManifest = PascalCaseWire<ReferenceSetManifest>
export type WireRenderArtifactRequest = PascalCaseWire<RenderArtifactRequest>
export type WireResolveSelectionRequest = PascalCaseWire<ResolveSelectionRequest>
export type WireResolveSelectionResponse = PascalCaseWire<ResolveSelectionResponse>
export type WireRunRequest = PascalCaseWire<RunRequest>
export type WireRuntimeIdentity = PascalCaseWire<RuntimeIdentity>
export type WireSelectionChange = PascalCaseWire<SelectionChange>
export type WireToolchainManifest = PascalCaseWire<ToolchainManifest>
export type WireTransformArtifactRequest = PascalCaseWire<TransformArtifactRequest>
export type WireUpdateGistRequest = PascalCaseWire<UpdateGistRequest>
export type WireUserExceptionInfo = PascalCaseWire<UserExceptionInfo>
export type WireVerifyArtifactRequest = PascalCaseWire<VerifyArtifactRequest>
export type WireWorkerError = PascalCaseWire<WorkerError>
export type WireWorkspaceFile = PascalCaseWire<WorkspaceFile>
export type WireWorkspaceSnapshot = PascalCaseWire<WorkspaceSnapshot>

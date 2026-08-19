namespace SharpLabNext.Worker.FSharp.Compiler

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Tokenization
open Microsoft.FSharp.Reflection

type CompilerDiagnosticSeverity =
    | Hidden = 0
    | Information = 1
    | Warning = 2
    | Error = 3

[<Sealed>]
type FSharpTextRange(
    startLine: int,
    startCharacter: int,
    endLine: int,
    endCharacter: int) =
    member _.StartLine = startLine
    member _.StartCharacter = startCharacter
    member _.EndLine = endLine
    member _.EndCharacter = endCharacter

[<Sealed>]
type FSharpCompilerDiagnostic(
    severity: CompilerDiagnosticSeverity,
    code: string,
    message: string,
    filePath: string,
    range: FSharpTextRange) =
    member _.Severity = severity
    member _.Code = code
    member _.Message = message
    member _.FilePath = filePath
    member _.Range = range

[<Sealed>]
type FSharpCompileResponse(
    diagnostics: FSharpCompilerDiagnostic array,
    terminatingException: string) =
    member _.Diagnostics = diagnostics
    member _.TerminatingException = terminatingException

[<Sealed>]
type FSharpAstNode(
    kind: string,
    range: FSharpTextRange,
    properties: IReadOnlyDictionary<string, string>,
    children: FSharpAstNode array) =
    member _.Kind = kind
    member _.Range = range
    member _.Properties = properties
    member _.Children = children

[<Sealed>]
type FSharpAstResponse(
    root: FSharpAstNode,
    diagnostics: FSharpCompilerDiagnostic array,
    truncated: bool) =
    member _.Root = root
    member _.Diagnostics = diagnostics
    member _.Truncated = truncated

[<Sealed>]
type FSharpProjectInput(
    projectFileName: string,
    sourceFiles: string array,
    otherOptions: string array,
    loadTimeUtc: DateTime) =
    member _.ProjectFileName = projectFileName
    member _.SourceFiles = sourceFiles
    member _.OtherOptions = otherOptions
    member _.LoadTimeUtc = loadTimeUtc

[<Sealed>]
type FSharpCompletion(
    name: string,
    nameInCode: string,
    detail: string,
    documentation: string,
    kind: string) =
    member _.Name = name
    member _.NameInCode = nameInCode
    member _.Detail = detail
    member _.Documentation = documentation
    member _.Kind = kind

[<AllowNullLiteral; Sealed>]
type FSharpHover(markdown: string, range: FSharpTextRange) =
    member _.Markdown = markdown
    member _.Range = range

[<Sealed>]
type FSharpSignatureParameter(label: string) =
    member _.Label = label

[<Sealed>]
type FSharpSignature(
    label: string,
    documentation: string,
    parameters: FSharpSignatureParameter array) =
    member _.Label = label
    member _.Documentation = documentation
    member _.Parameters = parameters

[<AllowNullLiteral; Sealed>]
type FSharpSignatureHelp(
    signatures: FSharpSignature array,
    activeParameter: int) =
    member _.Signatures = signatures
    member _.ActiveParameter = activeParameter

[<Sealed>]
type FSharpDocumentSymbol(
    name: string,
    detail: string,
    kind: string,
    range: FSharpTextRange,
    selectionRange: FSharpTextRange,
    children: FSharpDocumentSymbol array) =
    member _.Name = name
    member _.Detail = detail
    member _.Kind = kind
    member _.Range = range
    member _.SelectionRange = selectionRange
    member _.Children = children

[<Sealed>]
type FSharpSemanticClassification(kind: string, range: FSharpTextRange) =
    member _.Kind = kind
    member _.Range = range

[<Sealed>]
type FSharpSourceEdit(range: FSharpTextRange, newText: string) =
    member _.Range = range
    member _.NewText = newText

[<Sealed>]
type FSharpFileAnalysis(
    diagnostics: FSharpCompilerDiagnostic array,
    parseDiagnostics: FSharpCompilerDiagnostic array) =
    member _.Diagnostics = diagnostics
    member _.ParseDiagnostics = parseDiagnostics

module private Conversion =
    let toRange (value: range) =
        FSharpTextRange(
            max 0 (value.StartLine - 1),
            max 0 value.StartColumn,
            max 0 (value.EndLine - 1),
            max 0 value.EndColumn)

    let diagnosticSeverity = function
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> CompilerDiagnosticSeverity.Error
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> CompilerDiagnosticSeverity.Warning
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Info -> CompilerDiagnosticSeverity.Information
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Hidden -> CompilerDiagnosticSeverity.Hidden

    let diagnostic (value: FSharpDiagnostic) =
        FSharpCompilerDiagnostic(
            diagnosticSeverity value.Severity,
            value.ErrorNumberText,
            value.Message.Replace('\r', ' ').Replace('\n', ' '),
            value.FileName,
            toRange value.Range)

    let taggedText (parts: TaggedText array) =
        String.Concat(parts |> Array.map _.Text)

    let tooltip (ToolTipText elements) =
        elements
        |> Seq.collect (function
            | ToolTipElement.None -> Seq.empty
            | ToolTipElement.CompositionError error -> Seq.singleton error
            | ToolTipElement.Group group ->
                group
                |> Seq.map (fun item -> taggedText item.MainDescription))
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat "\n\n"

    let identifierAt (lineText: string) column =
        if String.IsNullOrEmpty lineText then
            0, 0, []
        else
            let mutable endColumn = min lineText.Length (max 0 column)
            if endColumn < lineText.Length &&
               (Char.IsLetterOrDigit lineText[endColumn] || lineText[endColumn] = '_' || lineText[endColumn] = '\'') then
                endColumn <- endColumn + 1
            while endColumn < lineText.Length &&
                  (Char.IsLetterOrDigit lineText[endColumn] || lineText[endColumn] = '_' || lineText[endColumn] = '\'') do
                endColumn <- endColumn + 1
            let mutable startColumn = endColumn
            while startColumn > 0 &&
                  (Char.IsLetterOrDigit lineText[startColumn - 1] ||
                   lineText[startColumn - 1] = '_' ||
                   lineText[startColumn - 1] = '\'' ||
                   lineText[startColumn - 1] = '.') do
                startColumn <- startColumn - 1
            let text = lineText.Substring(startColumn, endColumn - startColumn).Trim('.')
            let names =
                text.Split('.', StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
            startColumn, endColumn, names

module private AstConversion =
    type State(maxNodes: int, maxDepth: int, maxUtf8Bytes: int, maxPreviewCharacters: int, source: string) =
        let mutable nodes = 0
        let mutable bytes = 0
        let mutable truncated = false

        member _.MaxNodes = maxNodes
        member _.MaxDepth = maxDepth
        member _.MaxUtf8Bytes = maxUtf8Bytes
        member _.MaxPreviewCharacters = maxPreviewCharacters
        member _.Source = source
        member _.Truncated = truncated

        member _.TryConsume(kind: string) =
            let cost = Encoding.UTF8.GetByteCount kind + 64
            if nodes >= maxNodes || bytes + cost > maxUtf8Bytes then
                truncated <- true
                false
            else
                nodes <- nodes + 1
                bytes <- bytes + cost
                true

        member _.MarkTruncated() = truncated <- true

    let private isScalar (value: obj) =
        if isNull value then true
        else
            let valueType = value.GetType()
            valueType.IsPrimitive || valueType.IsEnum ||
            valueType = typeof<string> || valueType = typeof<decimal> ||
            valueType = typeof<Guid>

    let private scalarText (value: obj) =
        if isNull value then null
        else
            let text = Convert.ToString(value, Globalization.CultureInfo.InvariantCulture)
            if isNull text then null
            elif text.Length <= 256 then text
            else text.Substring(0, 256)

    let private syntaxType (valueType: Type) =
        not (isNull valueType.Namespace) &&
        (valueType.Namespace.StartsWith("FSharp.Compiler.Syntax", StringComparison.Ordinal) ||
         valueType.Namespace.StartsWith("FSharp.Compiler.SyntaxTrivia", StringComparison.Ordinal))

    let private tryRange (value: obj) =
        match value with
        | null -> None
        | :? range as valueRange -> Some valueRange
        | _ ->
            let valueType = value.GetType()
            [| "Range"; "range" |]
            |> Array.tryPick (fun name ->
                let property = valueType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Instance)
                if isNull property || property.PropertyType <> typeof<range> then None
                else
                    match property.GetValue value with
                    | :? range as propertyRange -> Some propertyRange
                    | _ -> None)

    let private preview (source: string) (valueRange: range) maxCharacters =
        let lines = source.Replace("\r\n", "\n").Split('\n')
        let startLine = max 0 (valueRange.StartLine - 1)
        let endLine = min (lines.Length - 1) (max startLine (valueRange.EndLine - 1))
        if startLine >= lines.Length || endLine < 0 then null
        else
            let builder = StringBuilder()
            for lineIndex in startLine .. endLine do
                if builder.Length < maxCharacters then
                    if builder.Length > 0 then builder.Append('\n') |> ignore
                    let line = lines[lineIndex]
                    let startColumn = if lineIndex = startLine then min line.Length valueRange.StartColumn else 0
                    let endColumn = if lineIndex = endLine then min line.Length valueRange.EndColumn else line.Length
                    if endColumn >= startColumn then
                        builder.Append(line.AsSpan(startColumn, endColumn - startColumn)) |> ignore
            if builder.Length > maxCharacters then builder.ToString(0, maxCharacters)
            else builder.ToString()

    let convert (source: string) (root: obj) maxNodes maxDepth maxUtf8Bytes maxPreviewCharacters =
        let state = State(maxNodes, maxDepth, maxUtf8Bytes, maxPreviewCharacters, source)

        let rec convertValue fieldName (fallbackRange: range option) depth (value: obj) =
            if isNull value || depth > state.MaxDepth then
                if depth > state.MaxDepth then state.MarkTruncated()
                None
            else
                let valueType = value.GetType()
                if not (syntaxType valueType) then None
                else
                    let kind, fields =
                        if FSharpType.IsUnion(valueType, true) then
                            let unionCase, values = FSharpValue.GetUnionFields(value, valueType, true)
                            unionCase.Name, Array.zip (unionCase.GetFields()) values
                        elif FSharpType.IsRecord(valueType, true) then
                            let fieldInfos = FSharpType.GetRecordFields(valueType, true)
                            valueType.Name, Array.zip fieldInfos (FSharpValue.GetRecordFields(value, true))
                        else
                            valueType.Name, [||]

                    if not (state.TryConsume kind) then None
                    else
                        let nodeRange = tryRange value |> Option.orElse fallbackRange
                        let properties = Dictionary<string, string>(StringComparer.Ordinal)
                        properties["type"] <- valueType.Name
                        if not (String.IsNullOrWhiteSpace fieldName) then properties["field"] <- fieldName
                        match nodeRange with
                        | Some valueRange ->
                            let text = preview state.Source valueRange state.MaxPreviewCharacters
                            if not (String.IsNullOrWhiteSpace text) then properties["textPreview"] <- text
                        | None -> ()

                        let children = ResizeArray<FSharpAstNode>()
                        let rec addChild childField (childValue: obj) =
                            if isNull childValue then ()
                            elif isScalar childValue then
                                if properties.Count < 48 then properties[childField] <- scalarText childValue
                            elif childValue :? range then ()
                            elif syntaxType (childValue.GetType()) then
                                match convertValue childField nodeRange (depth + 1) childValue with
                                | Some child -> children.Add child
                                | None -> ()
                            elif childValue :? IEnumerable then
                                let mutable index = 0
                                for item in childValue :?> IEnumerable do
                                    if not (isNull item) then
                                        addChild ($"{childField}[{index}]") item
                                    index <- index + 1
                            elif FSharpType.IsUnion(childValue.GetType(), true) then
                                let childCase, childFields = FSharpValue.GetUnionFields(childValue, childValue.GetType(), true)
                                if childCase.Name = "Some" && childFields.Length = 1 then
                                    addChild childField childFields[0]

                        for fieldInfo, fieldValue in fields do
                            addChild fieldInfo.Name fieldValue

                        let convertedRange =
                            nodeRange
                            |> Option.map Conversion.toRange
                            |> Option.defaultValue (FSharpTextRange(0, 0, 0, 0))
                        Some(FSharpAstNode(kind, convertedRange, properties, children.ToArray()))

        let fallbackRange = tryRange root
        let converted =
            convertValue "" fallbackRange 0 root
            |> Option.defaultValue (
                FSharpAstNode(
                    "ParsedInput",
                    FSharpTextRange(0, 0, 0, 0),
                    Dictionary<string, string>(),
                    [||]))
        converted, state.Truncated

[<Sealed>]
type FSharpCompilerFacade() =
    let checker =
        FSharpChecker.Create(
            projectCacheSize = 32,
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            keepAllBackgroundSymbolUses = true,
            enableBackgroundItemKeyStoreAndSemanticClassification = true,
            parallelReferenceResolution = true,
            captureIdentifiersWhenParsing = true)

    let projectOptions (input: FSharpProjectInput) =
        checker.GetProjectOptionsFromCommandLineArgs(
            input.ProjectFileName,
            Array.append input.OtherOptions input.SourceFiles,
            loadedTimeStamp = input.LoadTimeUtc)

    let parseAndCheck (input: FSharpProjectInput) fileName version sourceText = async {
        let options = projectOptions input
        let! parseResult, checkAnswer =
            checker.ParseAndCheckFileInProject(
                fileName,
                version,
                SourceText.ofString sourceText,
                options)
        return parseResult, checkAnswer
    }

    static member private RequiredAssemblyMetadata key =
        typeof<FSharpCompilerFacade>.Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        |> Seq.tryFind (fun attribute -> String.Equals(attribute.Key, key, StringComparison.Ordinal))
        |> Option.bind (fun attribute -> Option.ofObj attribute.Value)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp $"Assembly metadata '{key}' is missing.")

    static member CompilerVersion =
        FSharpCompilerFacade.RequiredAssemblyMetadata "SharpLabNext.FSharpCompilerServiceVersion"
    static member LoadedCompilerVersion =
        let version = typeof<FSharpChecker>.Assembly.GetName().Version
        if isNull version then "unknown"
        else $"{version.Major}.{version.Minor}.{version.Build}"
    static member FSharpCorePackageVersion =
        FSharpCompilerFacade.RequiredAssemblyMetadata "SharpLabNext.FSharpCoreVersion"
    static member FSharpCoreAssemblyPath = typeof<Microsoft.FSharp.Core.Unit>.Assembly.Location

    member _.CompileAsync(arguments: string array, cancellationToken: CancellationToken) : Task<FSharpCompileResponse> =
        task {
            let! diagnostics, terminatingException =
                checker.Compile(arguments)
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            let message =
                terminatingException
                |> Option.map (fun error -> error.GetType().Name + ": " + error.Message)
                |> Option.defaultValue null
            return FSharpCompileResponse(diagnostics |> Array.map Conversion.diagnostic, message)
        }

    member _.ParseAstAsync(
        input: FSharpProjectInput,
        fileName: string,
        sourceText: string,
        maxNodes: int,
        maxDepth: int,
        maxUtf8Bytes: int,
        maxPreviewCharacters: int,
        cancellationToken: CancellationToken) : Task<FSharpAstResponse> =
        task {
            let options = projectOptions input
            let parsingOptions, optionDiagnostics = checker.GetParsingOptionsFromProjectOptions options
            let! parseResult =
                checker.ParseFile(fileName, SourceText.ofString sourceText, parsingOptions)
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            let root, truncated =
                AstConversion.convert
                    sourceText
                    parseResult.ParseTree
                    maxNodes
                    maxDepth
                    maxUtf8Bytes
                    maxPreviewCharacters
            let diagnostics =
                Seq.append optionDiagnostics parseResult.Diagnostics
                |> Seq.map Conversion.diagnostic
                |> Seq.toArray
            return FSharpAstResponse(root, diagnostics, truncated)
        }

    member _.AnalyzeAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        cancellationToken: CancellationToken) : Task<FSharpFileAnalysis> =
        task {
            let! parseResult, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            let checkDiagnostics =
                match checkAnswer with
                | FSharpCheckFileAnswer.Aborted -> [||]
                | FSharpCheckFileAnswer.Succeeded results -> results.Diagnostics
            return FSharpFileAnalysis(
                checkDiagnostics |> Array.map Conversion.diagnostic,
                parseResult.Diagnostics |> Array.map Conversion.diagnostic)
        }

    member _.GetHashDirectivesAsync(
        input: FSharpProjectInput,
        fileName: string,
        sourceText: string,
        cancellationToken: CancellationToken) : Task<string array> =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let options = projectOptions input
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions options
            let! parseResult =
                checker.ParseFile(fileName, SourceText.ofString sourceText, parsingOptions)
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            let parsedDirectives =
                match parseResult.ParseTree with
                | ParsedInput.ImplFile implementation ->
                    implementation.HashDirectives
                    |> Seq.map (fun (ParsedHashDirective(name, _, _)) -> name)
                    |> Seq.toArray
                | ParsedInput.SigFile _ -> [||]
            let lines = sourceText.Replace("\r\n", "\n").Split('\n')
            let tokenizedDirectives =
                checker.TokenizeFile(sourceText)
                |> Array.mapi (fun lineIndex tokens ->
                    tokens
                    |> Array.mapi (fun tokenIndex token -> tokenIndex, token)
                    |> Array.choose (fun (tokenIndex, token) ->
                        if token.TokenName <> "HASH" || lineIndex >= lines.Length then None
                        else
                            let line = lines[lineIndex]
                            let startColumn = min line.Length (token.RightColumn + 1)
                            let endColumn =
                                if tokenIndex + 1 < tokens.Length then
                                    min line.Length tokens[tokenIndex + 1].LeftColumn
                                else line.Length
                            if endColumn <= startColumn then None
                            else
                                Some(line.Substring(startColumn, endColumn - startColumn).Trim())))
                |> Array.concat
            return Array.append parsedDirectives tokenizedDirectives |> Array.distinct
        }

    member _.GetCompletionsAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        line: int,
        character: int,
        maxItems: int,
        cancellationToken: CancellationToken) : Task<FSharpCompletion array> =
        task {
            let! parseResult, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return [||]
            | FSharpCheckFileAnswer.Succeeded results ->
                let lines = sourceText.Replace("\r\n", "\n").Split('\n')
                if line < 0 || line >= lines.Length then return [||]
                else
                    let lineText = lines[line]
                    let column = min lineText.Length (max 0 character)
                    let quickParseColumn = if column > 0 then column - 1 else column
                    let partialName = QuickParse.GetPartialLongNameEx(lineText, quickParseColumn)
                    let declarations =
                        results.GetDeclarationListInfo(Some parseResult, line + 1, lineText, partialName)
                    return
                        declarations.Items
                        |> Seq.truncate maxItems
                        |> Seq.map (fun item ->
                            FSharpCompletion(
                                item.NameInList,
                                item.NameInCode,
                                item.FullName,
                                Conversion.tooltip item.Description,
                                item.Kind.ToString()))
                        |> Seq.toArray
        }

    member _.GetHoverAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        line: int,
        character: int,
        cancellationToken: CancellationToken) : Task<FSharpHover> =
        task {
            let! _, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return null
            | FSharpCheckFileAnswer.Succeeded results ->
                let lines = sourceText.Replace("\r\n", "\n").Split('\n')
                if line < 0 || line >= lines.Length then return null
                else
                    let lineText = lines[line]
                    let startColumn, endColumn, names = Conversion.identifierAt lineText character
                    if List.isEmpty names then return null
                    else
                        let tooltip =
                            results.GetToolTip(
                                line + 1,
                                endColumn,
                                lineText,
                                names,
                                int FSharpTokenTag.Identifier)
                            |> Conversion.tooltip
                        if String.IsNullOrWhiteSpace tooltip then return null
                        else
                            return FSharpHover(
                                "```fsharp\n" + tooltip + "\n```",
                                FSharpTextRange(line, startColumn, line, endColumn))
        }

    member _.GetSignatureHelpAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        line: int,
        character: int,
        cancellationToken: CancellationToken) : Task<FSharpSignatureHelp> =
        task {
            let! _, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return null
            | FSharpCheckFileAnswer.Succeeded results ->
                let lines = sourceText.Replace("\r\n", "\n").Split('\n')
                if line < 0 || line >= lines.Length then return null
                else
                    let lineText = lines[line]
                    let column = min lineText.Length (max 0 character)
                    let openParen = lineText.LastIndexOf('(', max 0 (column - 1))
                    if openParen < 0 then return null
                    else
                        let _, _, names = Conversion.identifierAt lineText openParen
                        let methods = results.GetMethods(line + 1, openParen, lineText, Some names)
                        if methods.Methods.Length = 0 then return null
                        else
                            let activeParameter =
                                lineText.Substring(openParen + 1, max 0 (column - openParen - 1))
                                |> Seq.filter ((=) ',')
                                |> Seq.length
                            let signatures =
                                methods.Methods
                                |> Array.map (fun methodItem ->
                                    let parameters =
                                        methodItem.Parameters
                                        |> Array.map (fun parameter ->
                                            FSharpSignatureParameter(Conversion.taggedText parameter.Display))
                                    let label =
                                        methods.MethodName + "(" +
                                        (parameters |> Array.map _.Label |> String.concat ", ") +
                                        ") : " + Conversion.taggedText methodItem.ReturnTypeText
                                    FSharpSignature(
                                        label,
                                        Conversion.tooltip methodItem.Description,
                                        parameters))
                            return FSharpSignatureHelp(signatures, activeParameter)
        }

    member _.GetDocumentSymbolsAsync(
        input: FSharpProjectInput,
        fileName: string,
        sourceText: string,
        cancellationToken: CancellationToken) : Task<FSharpDocumentSymbol array> =
        task {
            let options = projectOptions input
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions options
            let! parseResult =
                checker.ParseFile(fileName, SourceText.ofString sourceText, parsingOptions)
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            let convertItem (item: NavigationItem) children =
                FSharpDocumentSymbol(
                    item.LogicalName,
                    item.UniqueName,
                    item.Kind.ToString(),
                    Conversion.toRange item.BodyRange,
                    Conversion.toRange item.Range,
                    children)
            return
                parseResult.GetNavigationItems().Declarations
                |> Array.map (fun declaration ->
                    let nested =
                        declaration.Nested
                        |> Array.map (fun item -> convertItem item [||])
                    convertItem declaration.Declaration nested)
        }

    member _.GetSemanticClassificationAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        cancellationToken: CancellationToken) : Task<FSharpSemanticClassification array> =
        task {
            let! _, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return [||]
            | FSharpCheckFileAnswer.Succeeded results ->
                cancellationToken.ThrowIfCancellationRequested()
                return
                    results.GetSemanticClassification(None)
                    |> Array.map (fun item ->
                        FSharpSemanticClassification(
                            item.Type.ToString(),
                            Conversion.toRange item.Range))
        }

    member _.GetUnusedOpenEditsAsync(
        input: FSharpProjectInput,
        fileName: string,
        version: int,
        sourceText: string,
        cancellationToken: CancellationToken) : Task<FSharpSourceEdit array> =
        task {
            let source = SourceText.ofString sourceText
            let! _, checkAnswer =
                parseAndCheck input fileName version sourceText
                |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return [||]
            | FSharpCheckFileAnswer.Succeeded results ->
                let! ranges =
                    UnusedOpens.getUnusedOpens(results, fun line -> source.GetLineString line)
                    |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
                return
                    ranges
                    |> Seq.distinctBy (fun item ->
                        item.StartLine, item.StartColumn, item.EndLine, item.EndColumn)
                    |> Seq.map (fun item -> FSharpSourceEdit(Conversion.toRange item, ""))
                    |> Seq.toArray
        }

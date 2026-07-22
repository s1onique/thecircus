module Circus.Tooling.NoForcePush.StaticPolicy

open System
open System.IO
open System.Text.RegularExpressions

/// Detect force options in git push commands.
let private forceOptionPattern = Regex(@"(?:\s|^)(--force|-f|-uf)(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect force-with-lease options.
let private forceWithLeasePattern = Regex(@"(?:\s|^)--force-with-lease(?:=\S+)?(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect force-if-includes options.
let private forceIfIncludesPattern = Regex(@"(?:\s|^)--force-if-includes(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect leading-plus refspec for force push.
let private leadingPlusRefspecPattern = Regex(@"(?:\s|^)\+\S+:refs/", RegexOptions.IgnoreCase)

/// Detect remote deletion options.
let private remoteDeletePattern = Regex(@"(?:\s|^)(--delete|-d)(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect empty-source deletion refspec (:refs/heads/x).
let private emptySourceDeletePattern = Regex(@"(?:\s|^):refs/", RegexOptions.IgnoreCase)

/// Detect mirror or prune options.
let private mirrorPrunePattern = Regex(@"(?:\s|^)--(?:mirror|prune)(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect no-verify option.
let private noVerifyPattern = Regex(@"(?:\s|^)--no-verify(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect send-pack invocation.
let private sendPackPattern = Regex(@"(?:\s|^)git\s+send-pack(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect hook bypass options.
let private hookBypassPattern = Regex(@"(?:\s|^)--(?:no-verify|verify)(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect dynamic push arguments (variables, $@, $*, etc.).
let private dynamicArgPattern = Regex(@"\$(?:[@*]|\{[^\}]+\}|[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)

/// Detect eval or shell indirection.
let private evalPattern = Regex(@"(?:\s|^)(?:eval|exec|sh\s+-c)\s+", RegexOptions.IgnoreCase)

/// Detect GitHub API force options.
let private ghForcePattern = Regex(@"(?:\s|^)--field\s+force=true(?:\s|$)", RegexOptions.IgnoreCase)

/// Detect GitHub API ref deletion via gh.
let private ghDeleteRefPattern = Regex(@"gh\s+api\s+.*(?:ref|git)\s+.*(?:delete|remove)", RegexOptions.IgnoreCase)

/// Detect curl DELETE requests against Git ref endpoints.
let private curlDeleteRefPattern = Regex(@"curl\s+.*DELETE\s+.*(?:ref|git)(?:\s|$)", RegexOptions.IgnoreCase)

/// Check if a command line contains force push options.
let containsForcePush (command: string) : bool =
    forceOptionPattern.IsMatch(command) ||
    forceWithLeasePattern.IsMatch(command) ||
    forceIfIncludesPattern.IsMatch(command)

/// Check if a command line contains leading-plus refspec.
let containsLeadingPlusRefspec (command: string) : bool =
    leadingPlusRefspecPattern.IsMatch(command)

/// Check if a command line contains remote deletion options.
let containsRemoteDelete (command: string) : bool =
    remoteDeletePattern.IsMatch(command)

/// Check if a command line contains empty-source deletion refspec.
let containsEmptySourceDelete (command: string) : bool =
    emptySourceDeletePattern.IsMatch(command)

/// Check if a command line contains mirror or prune.
let containsMirrorOrPrune (command: string) : bool =
    mirrorPrunePattern.IsMatch(command)

/// Check if a command line contains no-verify.
let containsNoVerify (command: string) : bool =
    noVerifyPattern.IsMatch(command)

/// Check if a command line contains send-pack.
let containsSendPack (command: string) : bool =
    sendPackPattern.IsMatch(command)

/// Check if a command contains hook bypass.
let containsHookBypass (command: string) : bool =
    hookBypassPattern.IsMatch(command)

/// Check if a command contains dynamic push arguments.
let containsDynamicArgs (command: string) : bool =
    dynamicArgPattern.IsMatch(command) &&
    (command.Contains("push") || command.Contains("git"))

/// Check if a command contains eval or shell indirection for git push.
let containsEvalIndirection (command: string) : bool =
    evalPattern.IsMatch(command) &&
    (command.Contains("push") || command.Contains("git"))

/// Check if a command contains GitHub API force.
let containsGhForce (command: string) : bool =
    ghForcePattern.IsMatch(command)

/// Check if a command contains GitHub API ref deletion.
let containsGhDeleteRef (command: string) : bool =
    ghDeleteRefPattern.IsMatch(command)

/// Check if a command contains curl DELETE against Git ref.
let containsCurlDeleteRef (command: string) : bool =
    curlDeleteRefPattern.IsMatch(command)

/// Is this a git push command (or equivalent)?
let isGitPushCommand (command: string) : bool =
    let normalized = command.ToLowerInvariant().Replace("\"", "").Replace("'", "")
    normalized.Contains("git push") ||
    normalized.Contains("git send-pack") ||
    normalized.Contains("gh api") ||
    (normalized.Contains("curl") && normalized.Contains("git"))

/// Analyze a command line and return a list of diagnostic IDs.
let analyzeCommand (command: string) : Types.DiagnosticId list =
    let diagnostics = ResizeArray<Types.DiagnosticId>()
    let normalized = command.Trim()

    if isGitPushCommand normalized then
        if forceOptionPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_001 (sprintf "force option detected: %s" command))
        
        if forceWithLeasePattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_001 (sprintf "force-with-lease detected: %s" command))
        
        if forceIfIncludesPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_001 (sprintf "force-if-includes detected: %s" command))
        
        if leadingPlusRefspecPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_002 (sprintf "leading-plus refspec: %s" command))
        
        if remoteDeletePattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_003 (sprintf "remote delete: %s" command))
        
        if emptySourceDeletePattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_004 (sprintf "empty-source delete: %s" command))
        
        if mirrorPrunePattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_005 (sprintf "mirror/prune: %s" command))
        
        if noVerifyPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_006 (sprintf "hook bypass: %s" command))
        
        if sendPackPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_008 (sprintf "send-pack: %s" command))
        
        // Dynamic argument detection
        if dynamicArgPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_007 (sprintf "dynamic arguments: %s" command))
        
        if evalPattern.IsMatch(normalized) then
            diagnostics.Add(Types.NFP_007 (sprintf "eval indirection: %s" command))
    
    // GitHub API detection (not limited to git push context)
    if ghForcePattern.IsMatch(normalized) then
        diagnostics.Add(Types.NFP_009 (sprintf "GitHub API force: %s" command))
    
    if ghDeleteRefPattern.IsMatch(normalized) then
        diagnostics.Add(Types.NFP_010 (sprintf "GitHub API ref delete: %s" command))
    
    if curlDeleteRefPattern.IsMatch(normalized) then
        diagnostics.Add(Types.NFP_010 (sprintf "curl DELETE ref: %s" command))

    List.ofSeq diagnostics

/// Positive cases - commands that should NOT be flagged.
let private positiveCases = [
    "git push origin main"
    "git push --atomic origin main"
    "git push --follow-tags origin main"
    "git fetch origin"
    "git rebase origin/main"
    "git commit --amend"
    "git reset --hard HEAD^"
    "git branch -D unpublished-local-branch"
]

/// Check if a command is a known permitted operation.
let isPermittedCommand (command: string) : bool =
    positiveCases |> List.exists (fun p -> command.Trim() = p)

/// Verify a single surface file and return diagnostics.
let verifySurfaceFile
    (root: string)
    (entry: Types.SurfaceEntry)
    : Result<Types.Diagnostic list, Types.DiagnosticId> =
    try
        let fullPath = Path.Combine(root, entry.Path)
        
        if not (File.Exists fullPath) then
            Error(Types.NFP_012 (sprintf "surface file missing: %s" entry.Path))
        else
            let content = File.ReadAllText(fullPath)
            let commands = CommandLexer.extractCommandsFromContent entry.ParserKind content entry.Path
            
            let diagnostics = ResizeArray<Types.Diagnostic>()
            
            for cmd in commands do
                let findings = analyzeCommand cmd.RawSource
                for finding in findings do
                    diagnostics.Add({
                        Types.Diagnostic.Id = finding
                        Types.Diagnostic.Path = entry.Path
                        Types.Diagnostic.Line = cmd.Line
                        Types.Diagnostic.Column = cmd.Column
                        Types.Diagnostic.NormalizedCommand = cmd.RawSource.Trim()
                    })
            
            Ok(List.ofSeq diagnostics)
    with ex ->
        Error(Types.NFP_013 (sprintf "failed to verify %s: %s" entry.Path ex.Message))

/// Run the full static policy verification.
let verify (root: string) : Types.StaticPolicyResult =
    let diagnostics = ResizeArray<Types.Diagnostic>()
    let errors = ResizeArray<string>()
    let mutable filesExamined = 0

    // First get tracked files for validation
    match SurfaceInventory.getTrackedFiles root with
    | Error e ->
        let msg = sprintf "%A" e
        errors.Add(msg)
        { Types.StaticPolicyResult.RepositoryRoot = root
          Types.StaticPolicyResult.FilesExamined = 0
          Types.StaticPolicyResult.Diagnostics = []
          Types.StaticPolicyResult.OperationalErrors = List.ofSeq errors }
    | Ok tracked ->
        // Read and validate inventory with tracked files context
        match SurfaceInventory.readInventory root with
        | Error e ->
            let msg = sprintf "%A" e
            errors.Add(msg)
            { Types.StaticPolicyResult.RepositoryRoot = root
              Types.StaticPolicyResult.FilesExamined = 0
              Types.StaticPolicyResult.Diagnostics = []
              Types.StaticPolicyResult.OperationalErrors = List.ofSeq errors }
        | Ok entries ->
            // Validate paths with tracked file context
            match SurfaceInventory.validatePaths root entries tracked with
            | Error e ->
                errors.Add(sprintf "inventory validation failed: %A" e)
            | Ok () ->
                // Verify each surface
                for entry in entries do
                    filesExamined <- filesExamined + 1
                    match verifySurfaceFile root entry with
                    | Ok findings ->
                        for f in findings do
                            diagnostics.Add(f)
                    | Error e ->
                        errors.Add(sprintf "%A" e)
            
            // Check for unclassified executables
            let unclassified = SurfaceInventory.findUnclassifiedExecutables root tracked entries
            for path in unclassified do
                diagnostics.Add({
                    Types.Diagnostic.Id = Types.NFP_011 path
                    Types.Diagnostic.Path = path
                    Types.Diagnostic.Line = 0
                    Types.Diagnostic.Column = 0
                    Types.Diagnostic.NormalizedCommand = ""
                })
            
            // Sort diagnostics deterministically by path, line, column, rule_id
            let sorted =
                diagnostics
                |> Seq.sortBy (fun d -> d.Path, d.Line, d.Column, d.RuleId)
                |> List.ofSeq

            { Types.StaticPolicyResult.RepositoryRoot = root
              Types.StaticPolicyResult.FilesExamined = filesExamined
              Types.StaticPolicyResult.Diagnostics = sorted
              Types.StaticPolicyResult.OperationalErrors = List.ofSeq errors }

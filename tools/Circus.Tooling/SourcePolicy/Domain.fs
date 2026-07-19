module Circus.Tooling.SourcePolicy.Domain

open Circus.Tooling.SourcePolicy.Language

type Finding = {
    Path: string
    Code: ViolationCode
    Line: int option
    Detail: string
    Rule: string
    Expected: string option
    Actual: string option
}

type BaselineEntry = {
    Path: string
    ViolationKind: string
    PhysicalLines: int
    Sha256: string
    Owner: string
    SuccessorAct: string
    Reason: string
}

type Classification = {
    Path: string
    Category: FileCategory
    Language: SourceLanguage
    Shebang: string
    PhysicalLines: int
    Sha256: string
    Executable: bool
}

type PerFile = { Classification: Classification; Findings: Finding list }

type VerificationOutcome = {
    RepositoryRoot: string
    FilesExamined: int
    BaselineEntries: int
    Findings: Finding list
}

type ReportFormat =
    | FormatHuman
    | FormatJson

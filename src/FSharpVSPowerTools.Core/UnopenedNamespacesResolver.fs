﻿namespace FSharpVSPowerTools

open System

type LongIdent = string

type Entity =
    { FullRelativeName: LongIdent
      Namespace: LongIdent option
      Name: LongIdent }
    override x.ToString() = sprintf "%A" x

[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module Entity =
    let getRelativeNamespace (targetNs: Idents) (sourceNs: Idents) =
        let rec loop index =
            if index > targetNs.Length - 1 then sourceNs.[index..]
            // target namespace is not a full parent of source namespace, keep the source ns as is
            elif index > sourceNs.Length - 1 then sourceNs
            elif targetNs.[index] = sourceNs.[index] then loop (index + 1)
            else sourceNs.[index..]
        if sourceNs.Length = 0 || targetNs.Length = 0 then sourceNs
        else loop 0

    let cutAutoOpenModules (autoOpenParent: Idents option) (candidateNs: Idents) =
        let nsCount = 
            match autoOpenParent with
            | Some parent when parent.Length > 0 -> 
                min (parent.Length - 1) candidateNs.Length
            | _ -> candidateNs.Length
        candidateNs.[0..nsCount - 1]

    let tryCreate (targetNamespace: Idents option) (targetScope: Idents) (ident: ShortIdent) (requiresQualifiedAccessParent: Idents option) 
                  (autoOpenParent: Idents option) (candidateNamespace: Idents option) (candidate: Idents) =
        if candidate.Length = 0 || candidate.[candidate.Length - 1] <> ident then None
        else Some candidate
        |> Option.bind (fun candidate ->
            let fullOpenableNs, restIdents =
                let openableNsCount =
                    match requiresQualifiedAccessParent with
                    | Some parent -> min parent.Length candidate.Length
                    | None -> candidate.Length
                candidate.[0..openableNsCount - 2], candidate.[openableNsCount - 1..]

            let openableNs = cutAutoOpenModules autoOpenParent fullOpenableNs

            let getRelativeNs ns =
                match targetNamespace, candidateNamespace with
                | Some targetNs, Some candidateNs when candidateNs = targetNs ->
                    getRelativeNamespace targetScope ns
                | None, _ -> getRelativeNamespace targetScope ns
                | _ -> ns

            let relativeNs = getRelativeNs openableNs

            match relativeNs, restIdents with
            | [||], [||] -> None
            | [||], [|_|] -> None
            | _ ->
                Some { FullRelativeName = String.Join (".", Array.append (getRelativeNs fullOpenableNs) restIdents)
                       Namespace = match relativeNs with [||] -> None | _ -> Some (String.Join (".", relativeNs))
                       Name = String.Join (".", restIdents) })

type Pos = 
    { Line: int
      Col: int }
       
type ScopeKind =
    | Namespace
    | TopModule
    | NestedModule
    | OpenDeclaration
    override x.ToString() = sprintf "%A" x

type InsertContext =
        { ScopeKind: ScopeKind
          Pos: Pos }

module Ast =
    open Microsoft.FSharp.Compiler
    open Microsoft.FSharp.Compiler.Ast

    type EndLine = int
        
    let getEntityKind (ast: ParsedInput) (pos: Range.pos) : EntityKind option =
        let (|ConstructorPats|) = function
            | Pats ps -> ps
            | NamePatPairs(xs, _) -> List.map snd xs

        let isPosInRange range = Range.rangeContainsPos range pos

        let ifPosInRange range f =
            if isPosInRange range then f()
            else None

        let rec walkImplFileInput (ParsedImplFileInput(_, _, _, _, _, moduleOrNamespaceList, _)) = 
            List.tryPick walkSynModuleOrNamespace moduleOrNamespaceList

        and walkSynModuleOrNamespace (SynModuleOrNamespace(_, _, decls, _, _, _, r)) =
            ifPosInRange r (fun _ -> List.tryPick walkSynModuleDecl decls)

        and walkAttribute (attr: SynAttribute) = 
            if isPosInRange attr.Range then Some EntityKind.Attribute 
            else None

        and walkTypar (Typar (ident, _, _)) = ifPosInRange ident.idRange (fun _ -> Some EntityKind.Type)

        and walkTyparDecl (SynTyparDecl.TyparDecl (attrs, typar)) = 
            List.tryPick walkAttribute attrs
            |> Option.orElse (walkTypar typar)
            
        and walkTypeConstraint = function
            | SynTypeConstraint.WhereTyparDefaultsToType (t1, t2, _) -> walkTypar t1 |> Option.orElse (walkType t2)
            | SynTypeConstraint.WhereTyparIsValueType(t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparIsReferenceType(t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparIsUnmanaged(t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparSupportsNull (t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparIsComparable(t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparIsEquatable(t, _) -> walkTypar t
            | SynTypeConstraint.WhereTyparSubtypeOfType(t, ty, _) -> walkTypar t |> Option.orElse (walkType ty)
            | SynTypeConstraint.WhereTyparSupportsMember(ts, sign, _) -> 
                List.tryPick walkTypar ts |> Option.orElse (walkMemberSig sign)
            | SynTypeConstraint.WhereTyparIsEnum(t, ts, _) -> walkTypar t |> Option.orElse (List.tryPick walkType ts)
            | SynTypeConstraint.WhereTyparIsDelegate(t, ts, _) -> walkTypar t |> Option.orElse (List.tryPick walkType ts)

        and walkPatWithKind (kind: EntityKind option) = function
            | SynPat.Ands (pats, _) -> List.tryPick walkPat pats
            | SynPat.Named(SynPat.Wild nameRange as pat, _, _, _, _) -> 
                if isPosInRange nameRange then None
                else walkPat pat
            | SynPat.Typed(pat, t, _) -> walkPat pat |> Option.orElse (walkType t)
            | SynPat.Attrib(pat, attrs, _) -> walkPat pat |> Option.orElse (List.tryPick walkAttribute attrs)
            | SynPat.Or(pat1, pat2, _) -> List.tryPick walkPat [pat1; pat2]
            | SynPat.LongIdent(_, _, typars, ConstructorPats pats, _, r) -> 
                ifPosInRange r (fun _ -> kind)
                |> Option.orElse (
                    typars 
                    |> Option.bind (fun (SynValTyparDecls (typars, _, constraints)) -> 
                        List.tryPick walkTyparDecl typars
                        |> Option.orElse (List.tryPick walkTypeConstraint constraints)))
                |> Option.orElse (List.tryPick walkPat pats)
            | SynPat.Tuple(pats, _) -> List.tryPick walkPat pats
            | SynPat.Paren(pat, _) -> walkPat pat
            | SynPat.ArrayOrList(_, pats, _) -> List.tryPick walkPat pats
            | SynPat.IsInst(t, _) -> walkType t
            | SynPat.QuoteExpr(e, _) -> walkExpr e
            | _ -> None

        and walkPat = walkPatWithKind None

        and walkBinding (SynBinding.Binding(_, _, _, _, attrs, _, _, pat, returnInfo, e, _, _)) =
            List.tryPick walkAttribute attrs
            |> Option.orElse (walkPat pat)
            |> Option.orElse (walkExpr e)
            |> Option.orElse (
                match returnInfo with
                | Some (SynBindingReturnInfo (t, _, _)) -> walkType t
                | None -> None)

        and walkInterfaceImpl (InterfaceImpl(_, bindings, _)) =
            List.tryPick walkBinding bindings

        and walkIndexerArg = function
            | SynIndexerArg.One e -> walkExpr e
            | SynIndexerArg.Two(e1, e2) -> List.tryPick walkExpr [e1; e2]

        and walkType = function
            | SynType.LongIdent ident -> ifPosInRange ident.Range (fun _ -> Some EntityKind.Type)
            | SynType.App(ty, _, types, _, _, _, _) -> 
                walkType ty |> Option.orElse (List.tryPick walkType types)
            | SynType.LongIdentApp(_, _, _, types, _, _, _) -> List.tryPick walkType types
            | SynType.Tuple(ts, _) -> ts |> List.tryPick (fun (_, t) -> walkType t)
            | SynType.Array(_, t, _) -> walkType t
            | SynType.Fun(t1, t2, _) -> walkType t1 |> Option.orElse (walkType t2)
            | SynType.WithGlobalConstraints(t, _, _) -> walkType t
            | SynType.HashConstraint(t, _) -> walkType t
            | SynType.MeasureDivide(t1, t2, _) -> walkType t1 |> Option.orElse (walkType t2)
            | SynType.MeasurePower(t, _, _) -> walkType t
            | _ -> None

        and walkClause (Clause(pat, e1, e2, _, _)) =
            walkPatWithKind (Some EntityKind.Type) pat 
            |> Option.orElse (walkExpr e2)
            |> Option.orElse (Option.bind walkExpr e1)

        and walkExprWithKind (parentKind: EntityKind option) = function
            | SynExpr.LongIdent (_, _, _, r) -> 
                if isPosInRange r then parentKind |> Option.orElse (Some FunctionOrValue) 
                else None
            | SynExpr.Paren (e, _, _, _) -> walkExpr e
            | SynExpr.Quote(_, _, e, _, _) -> walkExpr e
            | SynExpr.Typed(e, _, _) -> walkExpr e
            | SynExpr.Tuple(es, _, _) -> List.tryPick walkExpr es
            | SynExpr.ArrayOrList(_, es, _) -> List.tryPick walkExpr es
            | SynExpr.Record(_, _, fields, r) -> 
                ifPosInRange r (fun _ ->
                    fields |> List.tryPick (fun (_, e, _) -> e |> Option.bind walkExpr))
            | SynExpr.New(_, t, e, _) -> walkExpr e |> Option.orElse (walkType t)
            | SynExpr.ObjExpr(_, _, bindings, ifaces, _, _) -> 
                List.tryPick walkBinding bindings |> Option.orElse (List.tryPick walkInterfaceImpl ifaces)
            | SynExpr.While(_, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.For(_, _, e1, _, e2, e3, _) -> List.tryPick walkExpr [e1; e2; e3]
            | SynExpr.ForEach(_, _, _, _, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.ArrayOrListOfSeqExpr(_, e, _) -> walkExpr e
            | SynExpr.CompExpr(_, _, e, _) -> walkExpr e
            | SynExpr.Lambda(_, _, _, e, _) -> walkExpr e
            | SynExpr.MatchLambda(_, _, synMatchClauseList, _, _) -> 
                List.tryPick walkClause synMatchClauseList
            | SynExpr.Match(_, e, synMatchClauseList, _, _) -> 
                walkExpr e |> Option.orElse (List.tryPick walkClause synMatchClauseList)
            | SynExpr.Do(e, _) -> walkExpr e
            | SynExpr.Assert(e, _) -> walkExpr e
            | SynExpr.App(_, _, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.TypeApp(e, _, tys, _, _, _, _) -> 
                walkExprWithKind (Some EntityKind.Type) e |> Option.orElse (List.tryPick walkType tys)
            | SynExpr.LetOrUse(_, _, bindings, e, _) -> List.tryPick walkBinding bindings |> Option.orElse (walkExpr e)
            | SynExpr.TryWith(e, _, _, _, _, _, _) -> walkExpr e
            | SynExpr.TryFinally(e1, e2, _, _, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.Lazy(e, _) -> walkExpr e
            | SynExpr.Sequential(_, _, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.IfThenElse(e1, e2, e3, _, _, _, _) -> 
                List.tryPick walkExpr [e1; e2] |> Option.orElse (match e3 with None -> None | Some e -> walkExpr e)
            | SynExpr.Ident ident -> ifPosInRange ident.idRange (fun _ -> Some EntityKind.FunctionOrValue)
            | SynExpr.LongIdentSet(_, e, _) -> walkExpr e
            | SynExpr.DotGet(e, _, _, _) -> walkExpr e
            | SynExpr.DotSet(e, _, _, _) -> walkExpr e
            | SynExpr.DotIndexedGet(e, args, _, _) -> walkExpr e |> Option.orElse (List.tryPick walkIndexerArg args)
            | SynExpr.DotIndexedSet(e, args, _, _, _, _) -> walkExpr e |> Option.orElse (List.tryPick walkIndexerArg args)
            | SynExpr.NamedIndexedPropertySet(_, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.DotNamedIndexedPropertySet(e1, _, e2, e3, _) -> List.tryPick walkExpr [e1; e2; e3]
            | SynExpr.TypeTest(e, t, _) -> walkExpr e |> Option.orElse (walkType t)
            | SynExpr.Upcast(e, t, _) -> walkExpr e |> Option.orElse (walkType t)
            | SynExpr.Downcast(e, t, _) -> walkExpr e |> Option.orElse (walkType t)
            | SynExpr.InferredUpcast(e, _) -> walkExpr e
            | SynExpr.InferredDowncast(e, _) -> walkExpr e
            | SynExpr.AddressOf(_, e, _, _) -> walkExpr e
            | SynExpr.JoinIn(e1, _, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.YieldOrReturn(_, e, _) -> walkExpr e
            | SynExpr.YieldOrReturnFrom(_, e, _) -> walkExpr e
            | SynExpr.LetOrUseBang(_, _, _, _, e1, e2, _) -> List.tryPick walkExpr [e1; e2]
            | SynExpr.DoBang(e, _) -> walkExpr e
            | SynExpr.TraitCall (ts, sign, e, _) ->
                List.tryPick walkTypar ts 
                |> Option.orElse (walkMemberSig sign)
                |> Option.orElse (walkExpr e)
            | _ -> None

        and walkExpr = walkExprWithKind None

        and walkSimplePat = function
            | SynSimplePat.Attrib (pat, attrs, _) ->
                walkSimplePat pat |> Option.orElse (List.tryPick walkAttribute attrs)
            | SynSimplePat.Typed(pat, t, _) -> walkSimplePat pat |> Option.orElse (walkType t)
            | _ -> None

        and walkField (SynField.Field(attrs, _, _, t, _, _, _, _)) =
            List.tryPick walkAttribute attrs |> Option.orElse (walkType t)

        and walkValSig (SynValSig.ValSpfn(attrs, _, _, t, _, _, _, _, _, _, _)) =
            List.tryPick walkAttribute attrs |> Option.orElse (walkType t)

        and walkMemberSig = function
            | SynMemberSig.Inherit (t, _) -> walkType t
            | SynMemberSig.Member(vs, _, _) -> walkValSig vs
            | SynMemberSig.Interface(t, _) -> walkType t
            | SynMemberSig.ValField(f, _) -> walkField f
            | SynMemberSig.NestedType(SynTypeDefnSig.TypeDefnSig (info, repr, memberSigs, _), _) -> 
                walkComponentInfo info
                |> Option.orElse (walkTypeDefnSigRepr repr)
                |> Option.orElse (List.tryPick walkMemberSig memberSigs)

        and walkMember = function
            | SynMemberDefn.AbstractSlot (valSig, _, _) -> walkValSig valSig
            | SynMemberDefn.Member(binding, _) -> walkBinding binding
            | SynMemberDefn.ImplicitCtor(_, attrs, pats, _, _) -> 
                List.tryPick walkAttribute attrs |> Option.orElse (List.tryPick walkSimplePat pats)
            | SynMemberDefn.ImplicitInherit(t, e, _, _) -> walkType t |> Option.orElse (walkExpr e)
            | SynMemberDefn.LetBindings(bindings, _, _, _) -> List.tryPick walkBinding bindings
            | SynMemberDefn.Interface(t, members, _) -> 
                walkType t 
                |> Option.orElse (members |> Option.bind (List.tryPick walkMember))
            | SynMemberDefn.Inherit(t, _, _) -> walkType t
            | SynMemberDefn.ValField(field, _) -> walkField field
            | SynMemberDefn.NestedType(tdef, _, _) -> walkTypeDefn tdef
            | SynMemberDefn.AutoProperty(attrs, _, _, t, _, _, _, _, e, _, _) -> 
                List.tryPick walkAttribute attrs
                |> Option.orElse (Option.bind walkType t)
                |> Option.orElse (walkExpr e)
            | _ -> None

        and walkEnumCase (EnumCase(attrs, _, _, _, _)) = List.tryPick walkAttribute attrs

        and walkUnionCaseType = function
            | SynUnionCaseType.UnionCaseFields fields -> List.tryPick walkField fields
            | SynUnionCaseType.UnionCaseFullType(t, _) -> walkType t

        and walkUnionCase (UnionCase(attrs, _, t, _, _, _)) = 
            List.tryPick walkAttribute attrs |> Option.orElse (walkUnionCaseType t)

        and walkTypeDefnSimple = function
            | SynTypeDefnSimpleRepr.Enum (cases, _) -> List.tryPick walkEnumCase cases
            | SynTypeDefnSimpleRepr.Union(_, cases, _) -> List.tryPick walkUnionCase cases
            | SynTypeDefnSimpleRepr.Record(_, fields, _) -> List.tryPick walkField fields
            | SynTypeDefnSimpleRepr.TypeAbbrev(_, t, _) -> walkType t
            | _ -> None

        and walkComponentInfo (ComponentInfo(attrs, typars, constraints, _, _, _, _, r)) =
            ifPosInRange r (fun _ -> Some EntityKind.Type)
            |> Option.orElse (
                List.tryPick walkAttribute attrs
                |> Option.orElse (List.tryPick walkTyparDecl typars)
                |> Option.orElse (List.tryPick walkTypeConstraint constraints))

        and walkTypeDefnRepr = function
            | SynTypeDefnRepr.ObjectModel (_, defns, _) -> List.tryPick walkMember defns
            | SynTypeDefnRepr.Simple(defn, _) -> walkTypeDefnSimple defn

        and walkTypeDefnSigRepr = function
            | SynTypeDefnSigRepr.ObjectModel (_, defns, _) -> List.tryPick walkMemberSig defns
            | SynTypeDefnSigRepr.Simple(defn, _) -> walkTypeDefnSimple defn

        and walkTypeDefn (TypeDefn (info, repr, members, _)) =
            walkComponentInfo info
            |> Option.orElse (walkTypeDefnRepr repr)
            |> Option.orElse (List.tryPick walkMember members)

        and walkSynModuleDecl (decl: SynModuleDecl) =
            match decl with
            | SynModuleDecl.NamespaceFragment fragment -> walkSynModuleOrNamespace fragment
            | SynModuleDecl.NestedModule(_, modules, _, range) ->
                ifPosInRange range (fun _ -> List.tryPick walkSynModuleDecl modules)
            | SynModuleDecl.Open _ -> None
            | SynModuleDecl.Let (_, bindings, r) ->
                ifPosInRange r (fun _ -> List.tryPick walkBinding bindings)
            | SynModuleDecl.Types (types, _) -> List.tryPick walkTypeDefn types
            | _ -> None

        let res = 
            match ast with 
            | ParsedInput.SigFile _ -> None
            | ParsedInput.ImplFile input -> walkImplFileInput input
        //debug "%A" ast
        res

    type Col = int

    let inline private longIdentToIdents ident = ident |> Seq.map (fun x -> string x) |> Seq.toArray

    type Scope =
        { Idents: Idents
          Kind: ScopeKind }

    let tryFindNearestOpenStatementBlock (currentLine: int) (ast: ParsedInput) = 
        let result: (Scope * Pos) option ref = ref None
        let ns: string[] option ref = ref None
        let modules = ResizeArray<Idents * EndLine * Col>()  
        
        let addModule (longIdent: LongIdent) endLine col =
            modules.Add(longIdent |> List.map string |> List.toArray, endLine, col)

        let doRange kind (scope: LongIdent) line col =
            if line <= currentLine then
                match !result with
                | None -> 
                    result := Some ({ Idents = longIdentToIdents scope; Kind = kind },
                                    { Line = line; Col = col })
                | Some (oldScope, oldPos) ->
                    match kind, oldScope.Kind with
                    | (Namespace | NestedModule | TopModule), OpenDeclaration
                    | _ when oldPos.Line <= line ->
                        result := 
                            Some ({ Idents = 
                                        match scope with 
                                        | [] -> oldScope.Idents 
                                        | _ -> longIdentToIdents scope
                                    Kind = kind },
                                  { Line = line; Col = col })
                    | _ -> ()

        let getMinColumn (decls: SynModuleDecls) =
            match decls with
            | [] -> None
            | firstDecl :: _ -> 
                match firstDecl with
                | SynModuleDecl.NestedModule(_, _, _, r) -> Some r
                | SynModuleDecl.Let(_, _, r) -> Some r
                | SynModuleDecl.DoExpr(_, _, r) -> Some r
                | SynModuleDecl.Types(_, r) -> Some r
                | SynModuleDecl.Exception(_, r) -> Some r
                | SynModuleDecl.Open(_, r) -> Some r
                | SynModuleDecl.HashDirective(_, r) -> Some r
                | _ -> None
                |> Option.map (fun r -> r.StartColumn)


        let rec walkImplFileInput (ParsedImplFileInput(_, _, _, _, _, moduleOrNamespaceList, _)) = 
            List.iter (walkSynModuleOrNamespace []) moduleOrNamespaceList

        and walkSynModuleOrNamespace (parent: LongIdent) (SynModuleOrNamespace(ident, isModule, decls, _, _, _, range)) =
            if range.EndLine >= currentLine then
                match isModule, parent, ident with
                | false, _, _ -> ns := Some (longIdentToIdents ident)
                // top level module with "inlined" namespace like Ns1.Ns2.TopModule
                | true, [], _f :: _s :: _ -> 
                    let ident = longIdentToIdents ident
                    ns := Some (ident.[0..ident.Length - 2])
                | _ -> ()
                
                let fullIdent = parent @ ident

                let startLine =
                    if isModule then range.StartLine
                    else range.StartLine - 1

                let scopeKind =
                    match isModule, parent with
                    | true, [] -> TopModule
                    | true, _ -> NestedModule
                    | _ -> Namespace

                doRange scopeKind fullIdent startLine range.StartColumn
                addModule fullIdent range.EndLine range.StartColumn
                List.iter (walkSynModuleDecl fullIdent) decls

        and walkSynModuleDecl (parent: LongIdent) (decl: SynModuleDecl) =
            match decl with
            | SynModuleDecl.NamespaceFragment fragment -> walkSynModuleOrNamespace parent fragment
            | SynModuleDecl.NestedModule(ComponentInfo(_, _, _, ident, _, _, _, _), decls, _, range) ->
                let fullIdent = parent @ ident
                addModule fullIdent range.EndLine range.StartColumn
                if range.EndLine >= currentLine then
                    let moduleBodyIdentation = getMinColumn decls |> Option.getOrElse (range.StartColumn + 4)
                    doRange NestedModule fullIdent range.StartLine moduleBodyIdentation
                    List.iter (walkSynModuleDecl fullIdent) decls
            | SynModuleDecl.Open (_, range) -> doRange OpenDeclaration [] range.EndLine (range.StartColumn - 5)
            | _ -> ()

        match ast with 
        | ParsedInput.SigFile _ -> ()
        | ParsedInput.ImplFile input -> walkImplFileInput input

        let res =
            maybe {
                let! scope, pos = !result
                let ns = !ns |> Option.map longIdentToIdents
                return scope, ns, { pos with Line = pos.Line + 1 } 
            }
        //debug "[UnopenedNamespaceResolver] Ident, line, col = %A, AST = %A" (!result) ast
        //printfn "[UnopenedNamespaceResolver] Ident, line, col = %A, AST = %A" (!result) ast
        let modules = 
            modules 
            |> Seq.filter (fun (_, endLine, _) -> endLine < currentLine)
            |> Seq.sortBy (fun (m, _, _) -> -m.Length)
            |> Seq.toList

        fun (ident: ShortIdent) (requiresQualifiedAccessParent: Idents option, autoOpenParent: Idents option, 
                                 entityNamespace: Idents option, entity: Idents) ->
            res 
            |> Option.bind (fun (scope, ns, pos) -> 
                Entity.tryCreate ns scope.Idents ident requiresQualifiedAccessParent autoOpenParent entityNamespace entity 
                |> Option.map (fun entity -> entity, scope, pos))
            |> Option.map (fun (e, scope, pos) ->
                e,
                match modules |> List.filter (fun (m, _, _) -> entity |> Array.startsWith m ) with
                | [] -> { ScopeKind = scope.Kind; Pos = pos }
                | (_, endLine, startCol) :: _ ->
                    //printfn "All modules: %A, Win module: %A" modules m
                    let scopeKind =
                        match scope.Kind with
                        | TopModule -> NestedModule
                        | x -> x
                    { ScopeKind = scopeKind; Pos = { Line = endLine + 1; Col = startCol } })

[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module InsertContext =
    /// Corrents insertion line number based on pure text.
    // (because we cannot do it based on the untyped AST)
    let adjustInsertPoint (getLineStr: int -> string) ctx  =
        let getLineStr line = (getLineStr line).Trim()
        let line =
            match ctx.ScopeKind with
            | ScopeKind.TopModule ->
                if ctx.Pos.Line > 1 then
                    // it's an implicite module without any open declarations    
                    if not ((getLineStr (ctx.Pos.Line - 2)).StartsWith "module") then 1
                    else ctx.Pos.Line
                else 1
            | ScopeKind.Namespace ->
                // for namespaces the start line is start line of the first nested entity
                if ctx.Pos.Line > 1 then
                    [0..ctx.Pos.Line - 1]
                    |> List.mapi (fun i line -> i, getLineStr line)
                    |> List.tryPick (fun (i, lineStr) -> 
                        if lineStr.StartsWith "namespace" then Some i
                        else None)
                    |> function
                        // move to the next line below "namespace" and convert it to F# 1-based line number
                        | Some line -> line + 2 
                        | None -> ctx.Pos.Line
                else 1  
            | _ -> ctx.Pos.Line

        { ctx.Pos with Line = line }

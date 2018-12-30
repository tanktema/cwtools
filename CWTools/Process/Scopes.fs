namespace CWTools.Process

open NodaTime.TimeZones
open System
open CWTools.Localisation
open CWTools.Utilities.Position
open CWTools.Common
open Microsoft.FSharp.Reflection
module Scopes =
    open CWTools.Utilities.Utils
    open Microsoft.FSharp.Collections.Tagged
    type EffectMap<'S when 'S : comparison> = Map<string, Effect<'S>, InsensitiveStringComparer>
    type StringSet = Set<string, InsensitiveStringComparer>
    type UsageScopeContext<'S> = 'S list
    type ContextResult<'S> =
    | Found of string * ('S list)
    | LocNotFound of string
    //| Failed
    [<Struct>]
    type LocEntry<'S> = {
        key : string
        value : char option
        desc : string
        position : range
        scopes : ContextResult<'S> list
        refs : string list
    }

    // type IScopeContext<'S when 'S : comparison> =
    //     abstract CurrentScope : 'S
    //     abstract PopScope : 'S list
    //     abstract GetFrom : int -> 'S
    //     abstract Root : 'S
    //     abstract From :'S list
    //     abstract Scopes :'S list
    // type RuleContext<'T when 'T : comparison> =
    //     {
    //         subtypes : string list
    //         scopes : IScopeContext<'T>
    //         warningOnly : bool
    //     }

    type OutputScopeContext<'T> =
        {
            Root : 'T
            From : 'T list
            Scopes : 'T list
        }
        member inline this.CurrentScope = match this.Scopes with |[] -> None | x::_ -> Some x
        member inline this.PopScope : 'T list = match this.Scopes with |[] -> [] |_::xs -> xs
        member inline this.GetFrom i =
            if this.From.Length >= i then Some (this.From.Item (i - 1)) else None

    type ScopeContext< 'T when 'T :> IScope<'T> > =
        {
            Root : 'T
            From : 'T list
            Scopes : 'T list
        }
        member this.CurrentScope = match this.Scopes with |[] -> this.Root.AnyScope | x::_ -> x
        member this.PopScope : 'T list = match this.Scopes with |[] -> [] |_::xs -> xs
        member this.GetFrom i =
             if this.From.Length >= i then (this.From.Item (i - 1)) else this.Root.AnyScope




    type ScopeResult<'T when 'T :> IScope<'T>> =
        | NewScope of newScope : ScopeContext<'T> * ignoreKeys : string list
        | WrongScope of command : string * scope : 'T * expected : 'T list
        | NotFound
        | VarFound
        | VarNotFound of var : string
    // type EffectMap<'T> = Map<string, Effect<'T>, InsensitiveStringComparer>

    let createChangeScope<'T when 'T :> IScope<'T> and 'T : comparison > (oneToOneScopes) =
        (fun (varLHS : bool) (skipEffect : bool) (effects : EffectMap<_>) (triggers : EffectMap<_>) (vars : StringSet) (key : string) (source : ScopeContext<'T>) ->
            let key = if key.StartsWith("hidden:", StringComparison.OrdinalIgnoreCase) then key.Substring(7) else key
            if
                key.StartsWith("event_target:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("parameter:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("@", StringComparison.OrdinalIgnoreCase)
            then NewScope ({ Root = source.Root; From = source.From; Scopes = source.Root.AnyScope::source.Scopes }, [])
            else
                let key, varOnly = if key.StartsWith("var:", StringComparison.OrdinalIgnoreCase) then key.Substring(4), true else key, false
                let ampersandSplit = key.Split([|'@'|], 2)
                let keys = ampersandSplit.[0].Split('.')
                let keylength = keys.Length - 1
                let keys = keys |> Array.mapi (fun i k -> k, i = keylength)
                let inner ((context : ScopeContext<'T>), (changed : bool)) (nextKey : string) (last : bool) =
                    let onetoone = oneToOneScopes |> List.tryFind (fun (k, _) -> k == nextKey)
                    match onetoone with
                    | Some (_, f) -> f (context, false), NewScope (f (context, false) |> fst, [])
                    | None ->
                        let effectMatch = effects.TryFind nextKey |> Option.bind (function | :? ScopedEffect<'T> as e when (not skipEffect) || e.ScopeOnlyNotEffect  -> Some e |_ -> None)
                        let triggerMatch = triggers.TryFind nextKey |> Option.bind (function | :? ScopedEffect<'T> as e when (not skipEffect) || e.ScopeOnlyNotEffect -> Some e |_ -> None)
                        // let effect = (effects @ triggers)
                        //             |> List.choose (function | :? ScopedEffect as e -> Some e |_ -> None)
                        //             |> List.tryFind (fun e -> e.Name == nextKey)
                        // if skipEffect then (context, false), NotFound else
                        match Option.orElse effectMatch triggerMatch with
                        | None ->
                            if last && vars.Contains nextKey
                            then
                                (context, false), VarFound
                            else
                                if varOnly
                                then
                                    (context, false), VarNotFound nextKey
                                else
                                    (context, false), NotFound
                        | Some e ->
                            let possibleScopes = e.Scopes
                            let currentScope = context.CurrentScope :> IScope<_>
                            let exact = possibleScopes |> List.exists (fun x -> currentScope.MatchesScope x)
                            match context.CurrentScope, possibleScopes, exact, e.IsScopeChange with
                            | x, _, _, true when x = source.Root.AnyScope -> ({context with Scopes = e.InnerScope context.CurrentScope::context.Scopes}, true), NewScope ({source with Scopes = e.InnerScope context.CurrentScope::context.Scopes}, e.IgnoreChildren)
                            | x, _, _, false when x = source.Root.AnyScope-> (context, false), NewScope (context, e.IgnoreChildren)
                            | _, [], _, _ -> (context, false), NotFound
                            | _, _, true, true -> ({context with Scopes = e.InnerScope context.CurrentScope::context.Scopes}, true), NewScope ({source with Scopes = e.InnerScope context.CurrentScope::context.Scopes}, e.IgnoreChildren)
                            | _, _, true, false -> (context, false), NewScope (context, e.IgnoreChildren)
                            | current, ss, false, _ -> (context, false), WrongScope (nextKey, current, ss)
                let inner2 = fun a b l -> inner a b l |> (fun (c, d) -> c, Some d)
                let res = keys |> Array.fold (fun ((c,b), r) (k, l) -> match r with |None -> inner2 (c, b) k l |Some (NewScope (x, i)) -> inner2 (x, b) k l |Some x -> (c,b), Some x) ((source, false), None)// |> snd |> Option.defaultValue (NotFound)
                let res2 =
                    match res with
                    |(_, _), None -> NotFound
                    |(_, true), Some r -> r |> function |NewScope (x, i) -> NewScope ({ source with Scopes = x.CurrentScope::source.Scopes }, i) |x -> x
                    |(_, false), Some r -> r
                if ampersandSplit.Length > 1
                then
                    let keys = ampersandSplit.[1].Split('.')
                    let keylength = keys.Length - 1
                    let keys = keys |> Array.mapi (fun i k -> k, i = keylength)
                    let tres = keys |> Array.fold (fun ((c,b), r) (k, l) -> match r with |None -> inner2 (c, b) k l |Some (NewScope (x, i)) -> inner2 (x, b) k l |Some x -> (c,b), Some x) ((source, false), None)// |> snd |> Option.defaultValue (NotFound)
                    let tres2 =
                        match tres with
                        |(_, _), None -> NotFound
                        |(_, true), Some r -> r |> function |NewScope (x, i) -> NewScope ({ source with Scopes = x.CurrentScope::source.Scopes }, i) |x -> x
                        |(_, false), Some r -> r
                    match res2, tres2 with
                    |_, NotFound -> NotFound
                    |_, VarNotFound s -> VarNotFound s
                    |VarFound, _ -> VarFound
                    |_, _ -> NotFound
                else
                // let x = res |> function |NewScope x -> NewScope { source with Scopes = x.CurrentScope::source.Scopes } |x -> x
                // x
                res2)


    let createLocalisationCommandContext<'T when 'T :> IScope<'T> and 'T : comparison > (locPrimaryScopes : (string * (ScopeContext<'T> * bool -> ScopeContext<'T> * bool)) list) (scopedLocEffectsMap : EffectMap<'T>) =
        fun (commands : string list) (eventtargets : string list) (setvariables : string list) (entry : Entry) (command : string) ->
        let keys = command.Split('.') |> List.ofArray
        let inner ((first : bool), (rootScope : string), (scopes : 'T list)) (nextKey : string) =
            let onetoone = locPrimaryScopes |> List.tryFind (fun (k, _) -> k == nextKey)
            match onetoone with
            | Some (_) -> Found (nextKey, []), false
            | None ->
                let effectMatch = scopedLocEffectsMap.TryFind nextKey |> Option.bind (function | :? ScopedEffect<'T> as e -> Some e |_ -> None)
                match effectMatch with
                | Some e -> Found (rootScope, e.Scopes), false
                | None ->
                    let matchedCommand = (commands)  |> List.tryFind (fun c -> c == nextKey)
                    match matchedCommand, first, nextKey.StartsWith("parameter:", StringComparison.OrdinalIgnoreCase) with
                    |Some _, _, _ -> Found (rootScope, scopes), false
                    | _, _, true -> Found (rootScope, scopes), false
                    |None, false, false ->
                        match setvariables |> List.exists (fun sv -> sv == (nextKey.Split('@').[0])) with
                        | true -> Found (rootScope, scopes), false
                        | false -> LocNotFound (nextKey), false
                    |None, true, false ->
                        match eventtargets |> List.exists (fun et -> et == nextKey) with
                        | true -> Found (rootScope, scopes), false
                        | false -> LocNotFound (nextKey), false
        keys |> List.fold (fun r k -> match r with | (Found (r, s) , f) -> inner ((f, r, s)) k |LocNotFound s, _ -> LocNotFound s, false) (Found ("this", []), true) |> fst

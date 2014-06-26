﻿module internal Nessos.Vagrant.DependencyAnalysis

    open System
    open System.IO
    open System.Collections
    open System.Collections.Generic
    open System.Runtime.Serialization
    open System.Runtime.CompilerServices
    open System.Reflection

    open Nessos.FsPickler

    open Nessos.Vagrant.Utils
    open Nessos.Vagrant.AssemblyParser
    open Nessos.Vagrant.SliceCompilerTypes

    open Microsoft.FSharp.Reflection

    type TypeInfo =
        | Null
        | PartiallyComputed of isSealed:bool
        | Primitive
        | Array of isSealed:bool
        | Reference of isSealed:bool
        | GenericTypeDef
        | Named of isGenericInstance:bool * isSealed:bool * isISerializable:bool * nonSealedFields:FieldInfo []
    with
        member t.IsSealed =
            match t with
            | Null -> true
            | Primitive -> true
            | GenericTypeDef -> true // irrelevant to this algorithm
            | PartiallyComputed s
            | Reference s
            | Array s
            | Named (isSealed = s) -> s

        member t.IsNamedType =
            match t with
            | GenericTypeDef _
            | Named(isGenericInstance = false) -> true
            | _ -> false

    /// gathers all types that occur in an object graph

    let gatherObjectDependencies (obj : obj) : Type [] =

        let typeIndex = new Dictionary<Type, TypeInfo>()
        let objIndex = new ObjectIDGenerator()
        let inline add t info = typeIndex.[t] <- info ; info

        let isOptionType (t : Type) = 
            t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<int option>

        let isFsharpList (t : Type) =
            t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<int list>

        let isSealedType (t : Type) =
            t.IsSealed || FSharpType.IsTuple t || isOptionType t || isFsharpList t

        let isISerializable (t : Type) =
            let rec aux (t : Type) =
                if typeof<ISerializable>.IsAssignableFrom t then true
                else
                    match t.BaseType with
                    | null -> false
                    | bt -> aux bt
  
            aux t

        // walks up the type hierarchy, gathering all instance fields

        let gatherFields (t : Type) =
            // resolve conflicts, index by declaring type and field name
            let gathered = new Dictionary<Type * string, FieldInfo> ()

            let instanceFlags = 
                BindingFlags.NonPublic ||| BindingFlags.Public ||| 
                BindingFlags.Instance ||| BindingFlags.FlattenHierarchy

            let rec gather (t : Type) =
                let fields = t.GetFields(instanceFlags)
                for f in fields do
                    let k = f.DeclaringType, f.Name
                    if not <| gathered.ContainsKey k then
                        gathered.Add(k, f)

                match t.BaseType with
                | null -> ()
                | t when t = typeof<obj> -> ()
                | bt -> gather bt

            do gather t

            gathered |> Seq.map (function (KeyValue(_,f)) -> f) |> Seq.toArray


        // traverse field hierarchy of given type
        // decides if type is 'sealed', in the sense that it and all its instance fields are sealed.

        let rec traverseType (t : Type) =
            if t = null then Null else

            let found, info = typeIndex.TryGetValue t
            if found then info else

            do RuntimeHelpers.EnsureSufficientExecutionStack()

            if t.IsPrimitive || t = typeof<string> then 
                add t Primitive

            elif t.IsArray then
                let _ = add t <| PartiallyComputed false
                let info = traverseType <| t.GetElementType()
                add t <| Array info.IsSealed

            elif t.IsByRef || t.IsPointer then
                let _ = add t <| PartiallyComputed false
                let info = traverseType <| t.GetElementType()
                add t <| Reference info.IsSealed

            elif t.IsGenericTypeDefinition then
                add t GenericTypeDef
            else
                let isGenericInstance =
                    if t.IsGenericType then
                        let _ = traverseType <| t.GetGenericTypeDefinition()

                        for ga in t.GetGenericArguments() do
                            let _ = traverseType ga in ()

                        true
                    else
                        false

                if isISerializable t then
                    add t <| Named(isGenericInstance, false, true, [||])
                else
                    let info = add t <| PartiallyComputed (isSealedType t)
                    let fields = gatherFields t
                    let areAllFieldsSealed = fields |> Array.forall(fun f -> let info = traverseType f.FieldType in info.IsSealed)
                    let isSealed = info.IsSealed && areAllFieldsSealed

                    // re-update so that fields are correctly filtered in case of recursive pattern
                    let _ = add t <| PartiallyComputed isSealed
                    let fields' = fields |> Array.filter (fun f -> let info = traverseType f.FieldType in not info.IsSealed)

                    add t <| Named(isGenericInstance, isSealed, false, fields')

        // traverses the object graph: 
        // if non-sealed fields appear, evaluate and proceed accordingly.
                
        and traverseObj (obj : obj) =
            if Object.ReferenceEquals(obj, null) then () else
            let _,firstTime = objIndex.GetId obj
            if not firstTime then () else
    
            match obj with
            | :? Type as t -> traverseType t |> ignore
            | :? MemberInfo as m -> traverseType m.DeclaringType |> ignore
            | :? Assembly as a -> 
                match a.GetTypes() with
                | [||] -> ()
                | types -> traverseType (types.[0]) |> ignore

            | :? Delegate as d -> 
                traverseType (d.GetType()) |> ignore
                traverseType d.Method.DeclaringType |> ignore
                traverseObj d.Target
                for d' in d.GetInvocationList() do
                    if d <> d' then traverseObj d'
            | _ ->
                let t = obj.GetType()
                
                match traverseType t with
                | Array(isSealed = false) ->
                    for e in obj :?> Array do
                        traverseObj e

                | Named(isISerializable = true) ->
                    let sI = new SerializationInfo(t, new FormatterConverter())
                    let sC = new StreamingContext()
                    (obj :?> ISerializable).GetObjectData(sI, sC)
                    let enum = sI.GetEnumerator()
                    while enum.MoveNext() do
                        traverseObj enum.Value

                | Named(nonSealedFields = fields) ->
                    for f in fields do
                        let value = f.GetValue(obj)
                        traverseObj value

                | _ -> ()

        do traverseObj obj

        typeIndex 
        |> Seq.choose (function (KeyValue(t,info)) -> if info.IsNamedType then Some t else None)
        |> Seq.toArray

    /// recursively traverse assembly dependency graph

    let traverseDependencies (state : DynamicAssemblyCompilerState option) (assemblies : seq<Assembly>) =

        let isIgnoredAssembly =
            let getPublicKey (a : Assembly) = a.GetName().GetPublicKey()
            let systemPkt = [| getPublicKey typeof<int>.Assembly ; getPublicKey typeof<int option>.Assembly |]
            let vagrantAssemblies = 
                [| 
                    typeof<Mono.Cecil.AssemblyDefinition> ; 
                    typeof<Nessos.Vagrant.Cecil.IAssemblyParserConfig> ;
                    typeof<Nessos.Vagrant.AssemblyId>
                |] |> Array.map (fun t -> t.Assembly)

            fun (a:Assembly) ->
                Array.exists ((=) a) vagrantAssemblies ||
                    Array.exists ((=) (getPublicKey a)) systemPkt

        let tryResolveLoadedAssembly (an : AssemblyName) =
            match tryGetLoadedAssembly an.FullName with //, state with
            | Some a when isIgnoredAssembly a -> None
            | Some _ as s -> s
            | None ->
                match state |> Option.bind (fun s -> s.TryFindSliceInfo an.FullName) with
                // if a slice, return from compiler state directly as not loaded in AppDomain
                | Some (_,slice) -> Some slice.Assembly
                | None ->
                    try
                        // attempt loading from local machine
                        let a = Assembly.Load an
                        if isIgnoredAssembly a then None
                        else Some a
                    with :? FileNotFoundException -> None

        let rec traverseDependencyGraph (graph : Map<AssemblyId, Assembly * Assembly list>) (remaining : Assembly list) =
            match remaining with
            | [] -> graph |> Map.toList |> List.map snd
            | a :: tail when graph.ContainsKey a.AssemblyId || isIgnoredAssembly a -> traverseDependencyGraph graph tail
            | a :: tail -> 
                let dependencies = a.GetReferencedAssemblies() |> Array.choose tryResolveLoadedAssembly |> Array.toList
                traverseDependencyGraph (graph.Add(a.AssemblyId, (a, dependencies))) (dependencies @ tail)

        let dependencies =
            assemblies
            |> Seq.toList
            |> traverseDependencyGraph Map.empty
            |> getTopologicalOrdering

        // check for assemblies of identical qualified name
        match dependencies |> Seq.groupBy(fun a -> a.FullName) |> Seq.tryFind (fun (_,assemblies) -> Seq.length assemblies > 1) with
        | None -> ()
        | Some(name,_) -> 
            raise <| new VagrantException(sprintf "ran into duplicate assemblies of qualified name '%s'. This is not supported." name)

        dependencies


    /// parse a collection of assemblies, identify the dynamic assemblies that require slice compilation
    /// the dynamic assemblies are then parsed to Cecil and sorted topologically for correct compilation order.

    let parseDynamicAssemblies (state : DynamicAssemblyCompilerState) (assemblies : seq<Assembly>) =

        let isDynamicAssemblyRequiringCompilation (a : Assembly) =
            if a.IsDynamic then
                state.DynamicAssemblies.TryFind a.FullName 
                |> Option.forall (fun info -> info.HasFreshTypes)
            else
                false

        let rec traverse (graph : Map<AssemblyId, _>) (remaining : Assembly list) =
            match remaining with
            | [] -> graph
            | a :: rest when graph.ContainsKey a.AssemblyId || not <| isDynamicAssemblyRequiringCompilation a -> traverse graph rest
            | a :: rest ->
                // parse dynamic assembly
                let ((_,_,dependencies,_) as sliceData) = parseDynamicAssemblySlice state a

                let dependencies = dependencies |> List.choose tryGetLoadedAssembly

                let graph' = graph.Add(a.AssemblyId, (a, dependencies, sliceData))
                
                traverse graph' (rest @ dependencies)


        // topologically sort output
        let dynamicAssemblies = traverse Map.empty <| Seq.toList assemblies

        dynamicAssemblies
        |> Seq.map (function KeyValue(_, (a, deps,_)) -> a, deps |> List.filter (fun a -> a.IsDynamic))
        |> Seq.toList
        |> getTopologicalOrdering
        |> List.map (fun a -> let _,_,data = dynamicAssemblies.[a.AssemblyId] in data)



    type Dependencies = (Assembly * seq<Type>) list

    let computeDependencies (obj:obj) : Dependencies =
        gatherObjectDependencies obj 
        |> Seq.groupBy (fun t -> t.Assembly)
        |> Seq.toList

    /// determines the assemblies that require slice compilation based on given dependency input

    let getDynamicDependenciesRequiringCompilation (state : DynamicAssemblyCompilerState) (dependencies : Dependencies) =
        dependencies
        |> List.filter(fun (a,types) ->
            if a.IsDynamic then
                match state.DynamicAssemblies.TryFind a.FullName with
                | Some info -> types |> Seq.exists(fun t -> not <| info.TypeIndex.ContainsKey t.FullName)
                | None -> true
            else
                false)
        |> List.map fst


    /// reassigns assemblies so that the correct assembly slices are matched

    let remapDependencies (state : DynamicAssemblyCompilerState) (dependencies : Dependencies) =
        let remap (a : Assembly, ts : seq<Type>) =
            if a.IsDynamic then
                match state.DynamicAssemblies.TryFind a.FullName with
                | None -> raise <| new VagrantException(sprintf "no slices have been created for assembly '%s'." a.FullName)
                | Some info ->
                    let remapType (t : Type) =
                        match info.TypeIndex.TryFind t.FullName with
                        | None | Some (InNoSlice | InAllSlices) -> 
                            raise <| new VagrantException(sprintf "no slice corresponds to dynamic type '%O'." t)

                        | Some (InSpecificSlice slice) -> slice.Assembly

                    Seq.map remapType ts
            else Seq.singleton a

        dependencies |> Seq.collect remap |> traverseDependencies (Some state)
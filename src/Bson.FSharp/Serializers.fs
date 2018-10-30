namespace Bson.FSharp

open MongoDB.Bson.Serialization
open Microsoft.FSharp.Reflection
open System
open MongoDB.Bson.Serialization.Serializers
open MongoDB.Bson
open MongoDB.Bson.Serialization.Serializers

type ListSerializer<'T>() =
    inherit SerializerBase<'T list>()

    override __.Serialize(context, _, value) =
        let writer = context.Writer
        writer.WriteStartArray ()
        value |> List.iter (fun x -> BsonSerializer.Serialize (writer,typeof<'T>, x))
        writer.WriteEndArray ()

    override __.Deserialize(context, _) =
        let reader = context.Reader

        match reader.CurrentBsonType with
        | BsonType.Null -> reader.ReadNull(); []
        | BsonType.Array -> 
            seq {
                reader.ReadStartArray ()
                while reader.ReadBsonType() <> BsonType.EndOfDocument do
                    yield BsonSerializer.Deserialize<'T> (reader)
                reader.ReadEndArray ()
            } |> Seq.toList
        | bsonType -> 
            sprintf "Can't deserialize a %s from BsonType %s" typeof<'T list>.FullName (bsonType.ToString())
            |> InvalidOperationException
            |> raise

    interface IBsonArraySerializer with
        member __.TryGetItemSerializationInfo serializationInfo =
            let nominalType = typeof<'T>
            serializationInfo <- BsonSerializationInfo (null, BsonSerializer.LookupSerializer<'T> (), nominalType)
            true

type UnionCaseSerializer<'T>() =
    inherit SerializerBase<'T>()

    override __.Serialize(context, _, value) =
        let writer = context.Writer
        writer.WriteStartDocument ()
        let info, values = FSharpValue.GetUnionFields (value, typeof<'T>)
        writer.WriteName "_t"
        writer.WriteString info.Name
        writer.WriteName "_v"
        writer.WriteStartArray ()
        values |> Seq.zip (info.GetFields ()) |> Seq.iter (fun (field, value) ->
            BsonSerializer.Serialize (writer, field.PropertyType, value)
        )
        writer.WriteEndArray ()
        writer.WriteEndDocument()

    override __.Deserialize(context, _) =
        let reader = context.Reader
        
        reader.ReadStartDocument()
        let n = reader.ReadName (IO.Utf8NameDecoder.Instance)
        if n = "_t" then
            let typeName = reader.ReadString ()
            let unionType = 
                FSharpType.GetUnionCases (typeof<'T>)
                |> Seq.where (fun case -> case.Name = typeName) |> Seq.head
            reader.ReadStartArray ()
            let items = 
                (unionType.GetFields () |> Seq.map (fun f -> f.PropertyType))
                |> Seq.map (fun t ->
                    let serializer = BsonSerializer.LookupSerializer t
                    serializer.Deserialize context
                ) |> Seq.toArray
            reader.ReadEndArray()
            reader.ReadEndDocument()
            FSharpValue.MakeUnion (unionType, items) :?> 'T
        else
            failwith "No type information"

type OptionSerializer<'T>() =
    inherit SerializerBase<'T option>()

    override __.Serialize(context, _, value) =
        let writer = context.Writer
        match value with
        | Some v -> BsonSerializer.Serialize (writer, typeof<'T>, v)
        | None -> writer.WriteNull()

    override __.Deserialize(context, _) =
        let reader = context.Reader
        if reader.CurrentBsonType = BsonType.Null then
            reader.ReadNull()
            None
        else
            let serializer = BsonSerializer.LookupSerializer typeof<'T>
            let r = (serializer.Deserialize context :?> 'T)
            Some r

type FSharpSerializationProvider() =
    interface IBsonSerializationProvider with
        member __.GetSerializer(typ : Type) =
            if FSharpType.IsUnion typ then
                if typ.IsGenericType && typ.GetGenericTypeDefinition() = typedefof<Option<_>> then
                    typedefof<OptionSerializer<_>>.MakeGenericType(typ.GetGenericArguments())
                    |> Activator.CreateInstance :?> IBsonSerializer
                elif typ.IsGenericType && typ.GetGenericTypeDefinition() = typedefof<List<_>> then
                    typedefof<ListSerializer<_>>.MakeGenericType(typ.GetGenericArguments())
                    |> Activator.CreateInstance :?> IBsonSerializer
                else
                    typedefof<UnionCaseSerializer<_>>.MakeGenericType([|typ|])
                    |> Activator.CreateInstance :?> IBsonSerializer
            else
                null

module Registration =
    
    module private Mapping =

        let inline private isId str = "_id".Equals(str, StringComparison.OrdinalIgnoreCase) || 
                                       "id".Equals(str, StringComparison.OrdinalIgnoreCase)

        let inline private memberName (property: System.Reflection.PropertyInfo) = property.Name

        let registerClassMapForRecord<'T> additionalConfig =
            let ctor = FSharpValue.PreComputeRecordConstructorInfo(typeof<'T>)
            let fields = FSharpType.GetRecordFields (typeof<'T>)
            let fieldNames = fields |> Array.map (fun f -> f.Name)
            BsonClassMap.RegisterClassMap<'T>(System.Action<_> (fun cm ->
                cm.MapConstructor(ctor, fieldNames) |> ignore
                let (ids, properties) = fields |> Array.partition (memberName >> isId)
                ids |> Array.head |> cm.MapIdMember |> ignore
                properties |> Array.iter (cm.MapMember >> ignore)
                additionalConfig cm
            )) |> ignore

    let private _registerDefault = lazy (
            BsonSerializer.RegisterSerializationProvider(FSharpSerializationProvider())
            BsonSerializer.RegisterGenericSerializerDefinition(typeof<list<_>>, typeof<ListSerializer<_>>))

    let registerSerializers (customSerializers: IBsonSerializer list) = 
        customSerializers
        |> List.iter (fun s -> BsonSerializer.RegisterSerializer(s.ValueType, s))
        _registerDefault.Force ()
        
    let registerRecord<'T> additionalConfig = Mapping.registerClassMapForRecord additionalConfig
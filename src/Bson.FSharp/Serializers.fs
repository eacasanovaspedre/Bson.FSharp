namespace Bson.FSharp

open MongoDB.Bson.Serialization
open Microsoft.FSharp.Reflection
open System
open MongoDB.Bson.Serialization.Serializers
open MongoDB.Bson

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

    override __.Serialize(context, args, value) =
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

    override __.Deserialize(context, args) =
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
                    serializer.Deserialize (context)
                ) |> Seq.toArray
            reader.ReadEndArray()
            reader.ReadEndDocument()
            FSharpValue.MakeUnion (unionType, items) :?> 'T
        else
            failwith "No type information"

type FsharpSerializationProvider() =
    interface IBsonSerializationProvider with
        member __.GetSerializer(typ : Type) =
            if FSharpType.IsUnion typ then
                if typ.IsGenericType && typ.GetGenericTypeDefinition() = typedefof<List<_>> then
                     typedefof<ListSerializer<_>>.MakeGenericType(typ.GetGenericArguments())
                     |> Activator.CreateInstance :?> IBsonSerializer
                else
                    printfn "typ = %A" typ
                    typedefof<UnionCaseSerializer<_>>.MakeGenericType([|typ|])
                    |> Activator.CreateInstance :?> IBsonSerializer
            else
                null

module Registration =
    
    let private _register = lazy (
            BsonSerializer.RegisterSerializationProvider(FsharpSerializationProvider())
            BsonSerializer.RegisterGenericSerializerDefinition(typeof<list<_>>, typeof<ListSerializer<_>>))

    let register () = _register.Force ()
        
﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChoETL
{
    public interface IChoDynamicObjectRecordConfiguration
    { 
        ChoIgnoreFieldValueMode? IgnoreFieldValueMode { get; set; }
        HashSet<string> IgnoredFields { get; set; }
        object[] GetConvertersForType(Type fieldType);
    }

    public class ChoDynamicObjectConverter : JsonConverter, IChoJSONConverter
    {
        public static readonly ChoDynamicObjectConverter Instance = new ChoDynamicObjectConverter();

        private HashSet<string> _ignoreFields;

        /// <summary>
        /// Writes the JSON representation of the object.
        /// </summary>
        /// <param name="writer">The <see cref="JsonWriter"/> to write to.</param>
        /// <param name="value">The value.</param>
        /// <param name="serializer">The calling serializer.</param>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is ChoDynamicObject)
            {
                dynamic ctx = serializer.Context.Context;
                bool enableXmlAttributePrefix = ctx != null && ctx.EnableXmlAttributePrefix != null ? ctx.EnableXmlAttributePrefix : false;
                bool keepNSPrefix = ctx != null && ctx.keepNSPrefix != null ? ctx.keepNSPrefix : false;

                var obj = enableXmlAttributePrefix ? (value as ChoDynamicObject).AsXmlDictionary() : 
                    (value as ChoDynamicObject).AsDictionary(keepNSPrefix);
                
                var config = Context?.Configuration as IChoDynamicObjectRecordConfiguration;
                if (config != null && config.IgnoreFieldValueMode != null)
                {
                    _ignoreFields = config.IgnoredFields;

                    if (_ignoreFields != null)
                    {
                        foreach (var f in _ignoreFields)
                        {
                            if (obj.ContainsKey(f))
                                obj.Remove(f);
                        }
                    }

                    foreach (var key in obj.Keys.ToArray())
                    {
                        if ((config.IgnoreFieldValueMode | ChoIgnoreFieldValueMode.DBNull) == ChoIgnoreFieldValueMode.DBNull)
                        {
                            if (obj[key] == DBNull.Value)
                                obj.Remove(key);
                        }
                        else if ((config.IgnoreFieldValueMode | ChoIgnoreFieldValueMode.Empty) == ChoIgnoreFieldValueMode.Empty)
                        {
                            if (obj[key] is string && obj[key].ToNString().IsEmpty())
                                obj.Remove(key);
                        }
                        else if ((config.IgnoreFieldValueMode | ChoIgnoreFieldValueMode.Null) == ChoIgnoreFieldValueMode.Null)
                        {
                            if (obj[key] == null)
                                obj.Remove(key);
                        }
                        else if ((config.IgnoreFieldValueMode | ChoIgnoreFieldValueMode.WhiteSpace) == ChoIgnoreFieldValueMode.WhiteSpace)
                        {
                            if (obj[key] is string && obj[key].ToNString().IsNullOrWhiteSpace())
                                obj.Remove(key);
                        }
                    }
                }
                else if (serializer.NullValueHandling == NullValueHandling.Ignore)
                {
                    foreach (var key in obj.Keys)
                    {
                        if (obj[key] == null)
                            obj.Remove(key);
                    }
                }

                if (obj.Count > 0 || (obj.Count == 0 && serializer.NullValueHandling != NullValueHandling.Ignore))
                {
                    var t = serializer.SerializeToJToken(obj, dontUseConverter: true);
                    t.WriteTo(writer);
                }
            }
            else
            {
                var t = serializer.SerializeToJToken(value, dontUseConverter: true);
                t.WriteTo(writer);
            }
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        /// <param name="reader">The <see cref="JsonReader"/> to read from.</param>
        /// <param name="objectType">Type of the object.</param>
        /// <param name="existingValue">The existing value of object being read.</param>
        /// <param name="serializer">The calling serializer.</param>
        /// <returns>The object value.</returns>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var config = Context?.Configuration as IChoDynamicObjectRecordConfiguration;
            if (config != null)
            {
                _ignoreFields = config.IgnoredFields;
            }
            return ReadValue(reader);
        }

        private object ReadValue(JsonReader reader)
        {
            while (reader.TokenType == JsonToken.Comment)
            {
                if (!reader.Read())
                    throw new Exception("Unexpected end.");
            }

            switch (reader.TokenType)
            {
                case JsonToken.StartObject:
                    return ReadObject(reader);
                case JsonToken.StartArray:
                    return ReadList(reader);
                default:
                    if (IsPrimitiveToken(reader.TokenType))
                        return reader.Value;

                    throw new Exception("Unexpected token when converting ExpandoObject: {0}".FormatString(reader.TokenType));
            }
        }
        internal static bool IsPrimitiveToken(JsonToken token)
        {
            switch (token)
            {
                case JsonToken.Integer:
                case JsonToken.Float:
                case JsonToken.String:
                case JsonToken.Boolean:
                case JsonToken.Undefined:
                case JsonToken.Null:
                case JsonToken.Date:
                case JsonToken.Bytes:
                    return true;
                default:
                    return false;
            }
        }
        private object ReadList(JsonReader reader)
        {
            IList<object> list = new List<object>();

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.Comment:
                        break;
                    default:
                        object v = ReadValue(reader);

                        list.Add(v);
                        break;
                    case JsonToken.EndArray:
                        return list;
                }
            }

            throw new Exception("Unexpected end.");
        }

        private object ReadObject(JsonReader reader)
        {
            IDictionary<string, object> expandoObject = new ChoDynamicObject();

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        string propertyName = reader.Value.ToString();

                        if (!reader.Read())
                            throw new Exception("Unexpected end.");

                        object v = ReadValue(reader);
                        var itemType = v != null ? v.GetType() : null;
                        if (itemType != null)
                        {
                            object[] convs = null;
                            var config = Context?.Configuration as IChoDynamicObjectRecordConfiguration;
                            if (config != null)
                            {
                                convs = config.GetConvertersForType(itemType);
                            }

                            v = ChoConvert.ConvertFrom(v, typeof(object), null, convs);
                        }

                        if (_ignoreFields == null || !_ignoreFields.Contains(propertyName))
                        {
                            expandoObject[propertyName] = v;
                        }
                        break;
                    case JsonToken.Comment:
                        break;
                    case JsonToken.EndObject:
                        return expandoObject;
                }
            }

            throw new Exception("Unexpected end.");
        }

        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        /// <param name="objectType">Type of the object.</param>
        /// <returns>
        /// 	<c>true</c> if this instance can convert the specified object type; otherwise, <c>false</c>.
        /// </returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(ChoDynamicObject);
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="JsonConverter"/> can write JSON.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if this <see cref="JsonConverter"/> can write JSON; otherwise, <c>false</c>.
        /// </value>
        public override bool CanWrite
        {
            get { return true; }
        }

        public JsonSerializer Serializer { get; set; }
        public dynamic Context { get; set; }
    }
}

using GIsDataWorker.DTos;
using GIsDataWorker.Utailites;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;

namespace GIsDataWorker.Service;

/// <summary>
/// Provides location data retrieval from MongoDB collections,
/// mapping raw BSON documents to <see cref="MongoLocationDto"/> instances.
/// </summary>
public class MongoLocationService : IMongoLocationService
{
    /// <summary>
    /// Shared projection definition that limits returned fields to identity and coordinate fields,
    /// reducing network and deserialization overhead.
    /// </summary>
    private static readonly ProjectionDefinition<BsonDocument> s_projection =
        Builders<BsonDocument>.Projection
            .Include("_id")
            .Include("Name")
            .Include("Latitude")
            .Include("latitude")
            .Include("Lat")
            .Include("lat")
            .Include("Longitude")
            .Include("longitude")
            .Include("Lng")
            .Include("lng")
            .Include("Long")
            .Include("long");

    /// <summary>
    /// Shared find options applied to every collection query:
    /// batches of 1 000 documents, no cursor timeout, and the coordinate projection.
    /// </summary>
    private static readonly FindOptions<BsonDocument, BsonDocument> s_findOptions = new()
    {
        BatchSize = 1000,
        NoCursorTimeout = true,
        Projection = s_projection
    };

    /// <summary>The MongoDB database instance used for all collection queries.</summary>
    private readonly IMongoDatabase _database;

    /// <summary>The resolved list of collection names to iterate over.</summary>
    private readonly string[] _collectionNames;

    /// <summary>The field names to probe for latitude values, read from configuration.</summary>
    private readonly string[] _latitudeFieldNames;

    /// <summary>The field names to probe for longitude values, read from configuration.</summary>
    private readonly string[] _longitudeFieldNames;

    /// <summary>Logger used for diagnostic and debug output.</summary>
    private readonly ILogger<MongoLocationService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="MongoLocationService"/>.
    /// </summary>
    /// <param name="mongoSettings">
    /// Bound configuration options that supply the connection string, database name,
    /// and optional collection name overrides.
    /// </param>
    /// <param name="logger">Logger for this service.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="mongoSettings"/> or its <c>Value</c> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the connection string (<c>MongoSettings:Mongo</c>) or the database name
    /// (<c>MongoSettings:MongoDB</c>) is missing from configuration.
    /// </exception>
    public MongoLocationService(IOptions<MongoSettings> mongoSettings, ILogger<MongoLocationService> logger)
    {
        _logger = logger;

        var settings = mongoSettings?.Value ?? throw new ArgumentNullException(nameof(mongoSettings));

        if (string.IsNullOrWhiteSpace(settings.Mongo))
        {
            throw new InvalidOperationException("MongoSettings:Mongo is missing.");
        }

        if (string.IsNullOrWhiteSpace(settings.MongoDB))
        {
            throw new InvalidOperationException("MongoSettings:MongoDB is missing.");
        }

        _collectionNames = settings.Collections
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_collectionNames.Length == 0)
        {
            throw new InvalidOperationException("MongoSettings:Collections is empty or missing. Please configure collection names in appsettings.json.");
        }

        _latitudeFieldNames = settings.LatitudeFieldNames !;
        _longitudeFieldNames = settings.LongitudeFieldNames!;

        _database = new MongoClient(settings.Mongo).GetDatabase(settings.MongoDB);
    }

    /// <summary>
    /// Asynchronously streams all valid location documents from every configured MongoDB collection.
    /// </summary>
    /// <remarks>
    /// Documents are queried in batches (see <see cref="s_findOptions"/>).
    /// Documents whose coordinates cannot be parsed or are outside the valid geographic range
    /// (latitude −90 to 90, longitude −180 to 180) are silently skipped
    /// (or logged at <see cref="LogLevel.Debug"/> when that level is enabled).
    /// </remarks>
    /// <param name="cancellationToken">Token used to cancel the streaming operation.</param>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{T}"/> of <see cref="MongoLocationDto"/> objects,
    /// one per valid document across all collections.
    /// </returns>
    public async IAsyncEnumerable<MongoLocationDto> GetLocationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var collectionName in _collectionNames)
        {
            await foreach (var location in StreamCollectionLocationsAsync(collectionName, cancellationToken))
            {
                yield return location;
            }
        }
    }

    private async IAsyncEnumerable<MongoLocationDto> StreamCollectionLocationsAsync(string collectionName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var collection = _database.GetCollection<BsonDocument>(collectionName);

        using var cursor = await collection.FindAsync(FilterDefinition<BsonDocument>.Empty, s_findOptions, cancellationToken);

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var location in cursor.Current
                .Select(doc => MapLocation(collectionName, doc))
                .Where(loc => loc is not null))
            {
                yield return location!;
            }
        }
    }

    /// <summary>
    /// Maps a raw BSON <paramref name="document"/> to a <see cref="MongoLocationDto"/>,
    /// or returns <see langword="null"/> when coordinates are missing or out of range.
    /// </summary>
    /// <param name="collectionName">The source collection name, stored on the DTO for traceability.</param>
    /// <param name="document">The BSON document to map.</param>
    /// <returns>
    /// A <see cref="MongoLocationDto"/> when both coordinates are present and valid;
    /// otherwise <see langword="null"/>.
    /// </returns>
    private MongoLocationDto? MapLocation(string collectionName, BsonDocument document)
    {
        var latitude  = GetCoordinate(document, _latitudeFieldNames);
        var longitude = GetCoordinate(document, _longitudeFieldNames);

        if (latitude is null || longitude is null)
            return null;

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    "Skipping Mongo document {Id} because the coordinates are out of range: lat={Latitude}, lng={Longitude}.",
                    document.GetValue("_id", BsonNull.Value).ToString(),
                    latitude,
                    longitude);
            return null;
        }

        return new MongoLocationDto
        {
            Id = document.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty,
            CollectionName = collectionName,
            Name = document.TryGetValue("Name", out var nameValue) && nameValue.BsonType == BsonType.String
                ? nameValue.AsString
                : null,
            Latitude = latitude.Value,
            Longitude = longitude.Value
        };
    }

    /// <summary>
    /// Returns the first parseable <see cref="double"/> found by probing the document
    /// against an ordered list of candidate field names,
    /// or <see langword="null"/> when no matching field is present.
    /// </summary>
    private static double? GetCoordinate(BsonDocument document, string[] fieldNames) =>
        fieldNames
            .Select(name => document.TryGetValue(name, out var v) && !v.IsBsonNull
                ? BsonValueToDouble(v)
                : null)
            .FirstOrDefault(v => v.HasValue);

    /// <summary>
    /// Converts a <see cref="BsonValue"/> to a nullable <see cref="double"/>,
    /// supporting <see cref="BsonType.Double"/>, <see cref="BsonType.Int32"/>,
    /// <see cref="BsonType.Int64"/>, <see cref="BsonType.Decimal128"/>,
    /// and <see cref="BsonType.String"/> (invariant culture, then current culture as fallback).
    /// Returns <see langword="null"/> for all other types.
    /// </summary>
    private static double? BsonValueToDouble(BsonValue v) => v.BsonType switch
    {
        BsonType.Double     => v.AsDouble,
        BsonType.Int32      => (double)v.AsInt32,
        BsonType.Int64      => (double)v.AsInt64,
        BsonType.Decimal128 => (double)v.AsDecimal128,
        BsonType.String when double.TryParse(v.AsString, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        BsonType.String when double.TryParse(v.AsString, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) => d,
        _ => null
    };
}

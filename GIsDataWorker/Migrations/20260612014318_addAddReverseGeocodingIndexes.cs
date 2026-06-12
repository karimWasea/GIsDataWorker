using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GIsDataWorker.Migrations
{
    /// <inheritdoc />
    public partial class addAddReverseGeocodingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- region lookup: admin boundaries that contain the point ---------
            // Partial GiST so ST_Contains only ever scans administrative areas.
            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_polygon_admin_way
                    ON planet_osm_polygon USING gist (way)
                    WHERE boundary = 'administrative'
                      AND admin_level IS NOT NULL
                      AND name IS NOT NULL;",
                suppressTransaction: true);

            // Helps the admin_level filter / ordering on the candidate set.
            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_polygon_admin_level
                    ON planet_osm_polygon (admin_level)
                    WHERE boundary = 'administrative';",
                suppressTransaction: true);

            // --- nearby attractions: tourism / historic / leisure POIs ----------
            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_point_attraction_way
                    ON planet_osm_point USING gist (way)
                    WHERE name IS NOT NULL
                      AND (tourism IS NOT NULL OR historic IS NOT NULL OR leisure IS NOT NULL);",
                suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_polygon_attraction_way
                    ON planet_osm_polygon USING gist (way)
                    WHERE name IS NOT NULL
                      AND (tourism IS NOT NULL OR historic IS NOT NULL OR leisure IS NOT NULL);",
                suppressTransaction: true);

            // --- locality fallback: nearest populated place node ----------------
            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_point_place_way
                    ON planet_osm_point USING gist (way)
                    WHERE place IS NOT NULL AND name IS NOT NULL;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_polygon_admin_way;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_polygon_admin_level;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_point_attraction_way;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_polygon_attraction_way;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_point_place_way;", suppressTransaction: true);
        }
    }
}
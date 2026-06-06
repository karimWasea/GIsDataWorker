using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace GIsDataWorker.Models;

public partial class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

 
    public virtual DbSet<osm2pgsql_property> osm2pgsql_properties { get; set; }

    public virtual DbSet<planet_osm_line> planet_osm_lines { get; set; }

    public virtual DbSet<planet_osm_node> planet_osm_nodes { get; set; }

    public virtual DbSet<planet_osm_point> planet_osm_points { get; set; }

    public virtual DbSet<planet_osm_polygon> planet_osm_polygons { get; set; }

    public virtual DbSet<planet_osm_rel> planet_osm_rels { get; set; }

    public virtual DbSet<planet_osm_road> planet_osm_roads { get; set; }

    public virtual DbSet<planet_osm_way> planet_osm_ways { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<osm2pgsql_property>(entity =>
        {
            entity.HasKey(e => e.property).HasName("osm2pgsql_properties_pkey");
            entity.ToTable("osm2pgsql_properties", t => t.ExcludeFromMigrations()); // ✅
        });

        modelBuilder.Entity<planet_osm_line>(entity =>
        {
            entity.HasNoKey().ToTable("planet_osm_line", t => t.ExcludeFromMigrations()); // ✅
            entity.HasIndex(e => e.osm_id, "planet_osm_line_osm_id_idx");
            entity.HasIndex(e => e.way, "planet_osm_line_way_idx").HasMethod("gist");
            entity.Property(e => e._lock).HasColumnName("lock");
            entity.Property(e => e._operator).HasColumnName("operator");
            entity.Property(e => e._ref).HasColumnName("ref");
            entity.Property(e => e.addr_housename).HasColumnName("addr:housename");
            entity.Property(e => e.addr_housenumber).HasColumnName("addr:housenumber");
            entity.Property(e => e.addr_interpolation).HasColumnName("addr:interpolation");
            entity.Property(e => e.generator_source).HasColumnName("generator:source");
            entity.Property(e => e.tower_type).HasColumnName("tower:type");
            entity.Property(e => e.way).HasColumnType("geometry(LineString,3857)");
        });

        modelBuilder.Entity<planet_osm_node>(entity =>
        {
            entity.HasKey(e => e.id).HasName("planet_osm_nodes_pkey");
            entity.ToTable("planet_osm_nodes", t => t.ExcludeFromMigrations()); // ✅
            entity.Property(e => e.id).ValueGeneratedNever();
            entity.Property(e => e.tags).HasColumnType("jsonb");
        });

        modelBuilder.Entity<planet_osm_point>(entity =>
        {
            entity.HasNoKey().ToTable("planet_osm_point", t => t.ExcludeFromMigrations()); // ✅
            entity.HasIndex(e => e.osm_id, "planet_osm_point_osm_id_idx");
            entity.HasIndex(e => e.way, "planet_osm_point_way_idx").HasMethod("gist");
            entity.Property(e => e._lock).HasColumnName("lock");
            entity.Property(e => e._operator).HasColumnName("operator");
            entity.Property(e => e._ref).HasColumnName("ref");
            entity.Property(e => e.addr_housename).HasColumnName("addr:housename");
            entity.Property(e => e.addr_housenumber).HasColumnName("addr:housenumber");
            entity.Property(e => e.addr_interpolation).HasColumnName("addr:interpolation");
            entity.Property(e => e.generator_source).HasColumnName("generator:source");
            entity.Property(e => e.tower_type).HasColumnName("tower:type");
            entity.Property(e => e.way).HasColumnType("geometry(Point,3857)");
        });

        modelBuilder.Entity<planet_osm_polygon>(entity =>
        {
            entity.HasNoKey().ToTable("planet_osm_polygon", t => t.ExcludeFromMigrations()); // ✅
            entity.HasIndex(e => e.osm_id, "planet_osm_polygon_osm_id_idx");
            entity.HasIndex(e => e.way, "planet_osm_polygon_way_idx").HasMethod("gist");
            entity.Property(e => e._lock).HasColumnName("lock");
            entity.Property(e => e._operator).HasColumnName("operator");
            entity.Property(e => e._ref).HasColumnName("ref");
            entity.Property(e => e.addr_housename).HasColumnName("addr:housename");
            entity.Property(e => e.addr_housenumber).HasColumnName("addr:housenumber");
            entity.Property(e => e.addr_interpolation).HasColumnName("addr:interpolation");
            entity.Property(e => e.generator_source).HasColumnName("generator:source");
            entity.Property(e => e.tower_type).HasColumnName("tower:type");
            entity.Property(e => e.way).HasColumnType("geometry(Geometry,3857)");
        });

        modelBuilder.Entity<planet_osm_rel>(entity =>
        {
            entity.HasKey(e => e.id).HasName("planet_osm_rels_pkey");
            entity.ToTable("planet_osm_rels", t => t.ExcludeFromMigrations()); // ✅
            entity.Property(e => e.id).ValueGeneratedNever();
            entity.Property(e => e.members).HasColumnType("jsonb");
            entity.Property(e => e.tags).HasColumnType("jsonb");
        });

        modelBuilder.Entity<planet_osm_road>(entity =>
        {
            entity.HasNoKey().ToTable("planet_osm_roads", t => t.ExcludeFromMigrations()); // ✅
            entity.HasIndex(e => e.osm_id, "planet_osm_roads_osm_id_idx");
            entity.HasIndex(e => e.way, "planet_osm_roads_way_idx").HasMethod("gist");
            entity.Property(e => e._lock).HasColumnName("lock");
            entity.Property(e => e._operator).HasColumnName("operator");
            entity.Property(e => e._ref).HasColumnName("ref");
            entity.Property(e => e.addr_housename).HasColumnName("addr:housename");
            entity.Property(e => e.addr_housenumber).HasColumnName("addr:housenumber");
            entity.Property(e => e.addr_interpolation).HasColumnName("addr:interpolation");
            entity.Property(e => e.generator_source).HasColumnName("generator:source");
            entity.Property(e => e.tower_type).HasColumnName("tower:type");
            entity.Property(e => e.way).HasColumnType("geometry(LineString,3857)");
        });

        modelBuilder.Entity<planet_osm_way>(entity =>
        {
            entity.HasKey(e => e.id).HasName("planet_osm_ways_pkey");
            entity.ToTable("planet_osm_ways", t => t.ExcludeFromMigrations()); // ✅
            entity.Property(e => e.id).ValueGeneratedNever();
            entity.Property(e => e.tags).HasColumnType("jsonb");
        });

        OnModelCreatingPartial(modelBuilder);
    }
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}

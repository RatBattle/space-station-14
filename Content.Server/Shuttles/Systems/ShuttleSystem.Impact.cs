using System.Numerics;
using Content.Server.Shuttles.Components;
using Robust.Server.GameObjects;
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Map.Components;
using Content.Shared.Damage;
using Content.Shared.Buckle.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Slippery;
using Content.Shared.Inventory;
using Content.Shared.Clothing;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Server._NF.Shuttles.Components;
using System.Linq;
using Content.Shared.Tiles;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    [Dependency] private readonly MapSystem _mapSys = default!;
    [Dependency] private readonly DamageableSystem _damageSys = default!;
    [Dependency] private readonly InventorySystem _inventorySystem = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;

    /// <summary>
    /// Минимальная разница скоростей между двумя телами, при которой происходит "удар" шаттла.
    /// </summary>
    private const int MinimumImpactVelocity = 20;

    /// <summary>
    /// Кинетическая энергия, необходимая для разрушения одной плитки.
    /// </summary>
    private const float TileBreakEnergy = 2900;

    /// <summary>
    /// Кинетическая энергия, необходимая для создания искр.
    /// </summary>
    private const float SparkEnergy = 2000;

    /// <summary>
    /// Множитель для распределения энергии по площади контакта
    /// </summary>
    private const float EnergyDistributionMultiplier = 5f;

    private const int MaxContactArea = 10;
    private const float MaxContactDistance = 0.1f;

    private readonly SoundCollectionSpecifier _shuttleImpactSound = new("ShuttleImpactSound");

    private void InitializeImpact()
    {
        SubscribeLocalEvent<ShuttleComponent, StartCollideEvent>(OnShuttleCollide);
    }

    private void OnShuttleCollide(EntityUid uid, ShuttleComponent component, ref StartCollideEvent args)
    {
        if (!TryComp<MapGridComponent>(uid, out var ourGrid) ||
            !TryComp<MapGridComponent>(args.OtherEntity, out var otherGrid))
            return;

        var ourBody = args.OurBody;
        var otherBody = args.OtherBody;

        var ourXform = Transform(uid);
        if (ourXform.MapUid == null)
            return;
        // Проверка на null для ourBody и otherBody
        if (ourBody == null || otherBody == null)
        {
            return;
        }
        var otherXform = Transform(args.OtherEntity);

        var ourPoint = _transform.GetInvWorldMatrix(ourXform).Translation;
        var otherPoint = _transform.GetInvWorldMatrix(otherXform).Translation;
        var ourVelocity = _physics.GetLinearVelocity(uid, ourPoint, ourBody, ourXform);
        var otherVelocity = _physics.GetLinearVelocity(args.OtherEntity, otherPoint, otherBody, otherXform);
        var jungleDiff = (ourVelocity - otherVelocity).Length();

        if (jungleDiff < MinimumImpactVelocity)
            return;

        if (TryComp<ProtectedGridComponent>(uid, out var ourProtected) && ourProtected.NoGridCollision ||
        TryComp<ProtectedGridComponent>(args.OtherEntity, out var otherProtected) && otherProtected.NoGridCollision)
        {
            return; // Protected grids don't collide.
        }

        var energy = ourBody.Mass * jungleDiff * jungleDiff;
        var dir = (ourVelocity - otherVelocity).Normalized();

        // Распределяем энергию по нескольким плиткам
        var ourContactArea = CalculateContactArea(ourGrid, args.WorldPoint, dir);
        var otherContactArea = CalculateContactArea(otherGrid, args.WorldPoint, -dir);

        var ourEnergyPerTile = energy / ourContactArea.Count * EnergyDistributionMultiplier;
        var otherEnergyPerTile = energy / otherContactArea.Count * EnergyDistributionMultiplier;

        foreach (var tile in ourContactArea)
        {
            ProcessTile(uid, ourGrid, tile, ourEnergyPerTile, -dir);
        }

        foreach (var tile in otherContactArea)
        {
            ProcessTile(args.OtherEntity, otherGrid, tile, otherEnergyPerTile, dir);
        }

        var coordinates = new EntityCoordinates(ourXform.MapUid.Value, args.WorldPoint);
        var volume = MathF.Max(0f, MathF.Min(10f, MathF.Pow(jungleDiff, 0.5f) - 5f));
        var audioParams = AudioParams.Default.WithVariation(SharedContentAudioSystem.DefaultVariation).WithVolume(volume);
        _audio.PlayPvs(_shuttleImpactSound, coordinates, audioParams);

        // Knockdown unbuckled entities on both grids
        KnockdownEntitiesOnGrid(uid);
        KnockdownEntitiesOnGrid(args.OtherEntity);
    }

    /// <summary>
    /// Knocks down all unbuckled entities on the specified grid.
    /// </summary>
    private void KnockdownEntitiesOnGrid(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        // Find all entities on the grid
        var buckleQuery = GetEntityQuery<BuckleComponent>();
        var noSlipQuery = GetEntityQuery<NoSlipComponent>();
        var magbootsQuery = GetEntityQuery<MagbootsComponent>();
        var itemToggleQuery = GetEntityQuery<ItemToggleComponent>();
        var knockdownTime = TimeSpan.FromSeconds(5);

        // Get all entities with MobState component on the grid
        var query = EntityQueryEnumerator<MobStateComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var mobState, out var xform))
        {
            // Skip entities not on this grid
            if (xform.GridUid != gridUid)
                continue;

            // If entity has a buckle component and is buckled, skip it
            if (buckleQuery.TryGetComponent(uid, out var buckle) && buckle.Buckled)
                continue;

            // Skip if the entity directly has NoSlip component
            if (noSlipQuery.HasComponent(uid))
                continue;

            // Check if the entity is wearing magboots
            bool hasMagboots = false;

            // Check if they're wearing shoes with NoSlip component or activated magboots
            if (_inventorySystem.TryGetSlotEntity(uid, "shoes", out var shoes))
            {
                // If shoes have NoSlip component
                if (noSlipQuery.HasComponent(shoes))
                {
                    hasMagboots = true;
                }
                // If shoes are magboots and they are activated
                else if (magbootsQuery.HasComponent(shoes) &&
                         itemToggleQuery.TryGetComponent(shoes, out var toggle) &&
                         toggle.Activated)
                {
                    hasMagboots = true;
                }
            }

            if (hasMagboots)
                continue;

            // Apply knockdown to unbuckled entities
            _stuns.TryKnockdown(uid, knockdownTime, true);
        }
    }

    /*/// <summary>
    /// Checks if a grid has any entities with GridShieldProtectedEntityComponent on it
    /// </summary>
    private bool IsGridProtected(EntityUid gridUid)
    {
        // If the grid itself has a protection component, it's protected
        if (HasComp<GridShieldProtectionComponent>(gridUid))
            return true;

        // Also check for any individual entities with protection
        var protectedQuery = GetEntityQuery<GridShieldProtectedEntityComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        // Check all entities with the protection component
        var query = EntityQueryEnumerator<GridShieldProtectedEntityComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (xformQuery.TryGetComponent(uid, out var xform) && xform.GridUid == gridUid)
                return true;
        }

        return false;
    }*/

    /// <summary>
    /// Processes a zone of tiles around the impact point
    /// </summary>
    private void ProcessImpactZone(EntityUid uid, MapGridComponent grid, Vector2i centerTile, float energy, Vector2 dir, int radius)
    {
        // Skip processing if the grid has an anchor component
        if (HasComp<PreventGridAnchorChangesComponent>(uid) || HasComp<ForceAnchorComponent>(uid))
            return;

        // // Skip processing if this grid has entities protected by grid shields
        // if (IsGridProtected(uid))
        //     return;

        // Create damage object for entities
        DamageSpecifier damage = new();
        damage.DamageDict = new() { { "Blunt", energy } };

        // Process tiles in a circle around the impact point
        for (var x = -radius; x <= radius; x++)
        {
            for (var y = -radius; y <= radius; y++)
            {
                // Skip tiles too far from impact center (creating a rough circle)
                if (x*x + y*y > radius*radius)
                    continue;

                Vector2i tile = new Vector2i(centerTile.X + x, centerTile.Y + y);

                // Calculate distance-based energy falloff
                float distanceFactor = 1.0f - (float)Math.Sqrt(x*x + y*y) / (radius + 1);
                float tileEnergy = energy * distanceFactor;

                // Process entities on this tile
                foreach (EntityUid localUid in _lookup.GetLocalEntitiesIntersecting(uid, tile, gridComp: grid))
                {
                    // Skip entities protected by grid shields or entities that no longer exist
                    if (!Exists(localUid))
                        continue;

                    // Scale damage based on distance from center
                    var scaledDamage = new DamageSpecifier(damage) { DamageDict = { ["Blunt"] = tileEnergy } };
                    _damageSys.TryChangeDamage(localUid, scaledDamage);

                    // Check if the entity has a transform component before proceeding
                    if (!TryComp<TransformComponent>(localUid, out var form))
                        continue;

                    if (!form.Anchored)
                        _transform.Unanchor(localUid, form);

                    // Safely try to throw the entity
                    if (Exists(localUid))
                        _throwing.TryThrow(localUid, dir * distanceFactor); // Reduce throw force based on distance
                }

                // Only break tiles if they have enough energy
                if (tileEnergy > TileBreakEnergy)
                    _mapSys.SetTile(new Entity<MapGridComponent>(uid, grid), tile, Tile.Empty);

                // Spawn sparks with probability based on energy
                if (tileEnergy > SparkEnergy && distanceFactor > 0.7f)
                {
                    var coords = grid.GridTileToLocal(tile);
                    Spawn("EffectSparks", coords);
                }
            }
        }
    }

    private void ProcessTile(EntityUid uid, MapGridComponent grid, Vector2i tile, double energy, Vector2 dir)
    {
        DamageSpecifier damage = new();
        damage.DamageDict = new() { { "Blunt", energy} };

        foreach (EntityUid localUid in _lookup.GetLocalEntitiesIntersecting(uid, tile, gridComp: grid))
        {
            _damageSys.TryChangeDamage(localUid, damage);

            TransformComponent form = Transform(localUid);
            if (!form.Anchored)
            {
                _transform.Unanchor(localUid, form);
                _throwing.TryThrow(localUid, dir);
            }
        }

        // Получаем плитку из TileRef
        var tileRef = _mapSys.GetTileRef(grid.Owner, grid, tile);
        var currentTile = tileRef.Tile;

        // Проверяем, что плитка не пустая
        if (energy > TileBreakEnergy && !currentTile.IsEmpty)
        {
            _mapSys.SetTile(new Entity<MapGridComponent>(grid.Owner, grid), tile, Tile.Empty);
        }
        else if (energy > SparkEnergy)
        {
            SpawnAtPosition("EffectSparks", new EntityCoordinates(grid.Owner, tile));
        }
    }

    /// <summary>
    /// Вычисляет область контакта между двумя гридами.
    /// </summary>
    private List<Vector2i> CalculateContactArea(MapGridComponent grid, Vector2 worldPoint, Vector2 dir)
    {
        var contactArea = new List<Vector2i>();
        var initialTile = grid.WorldToTile(worldPoint);

        // Улучшенный поиск начальной точки
        if (_mapSys.GetTileRef(grid.Owner, grid, initialTile).Tile.IsEmpty)
        {
            initialTile = FindNearestNonEmptyTile(grid, initialTile);
        }

        if (initialTile == Vector2i.Zero)
            return contactArea;

        var checkedTiles = new HashSet<Vector2i>();
        var queue = new Queue<Vector2i>();

        contactArea.Add(initialTile);
        checkedTiles.Add(initialTile);
        queue.Enqueue(initialTile);

        // Получаем полигон грида
        var gridPolygon = GetGridPolygon(grid);

        while (queue.Count > 0 && contactArea.Count < MaxContactArea)
        {
            var currentTile = queue.Dequeue();

            var neighborTiles = new[]
            {
                currentTile + new Vector2i(-1, -1),
                currentTile + new Vector2i(-1, 0),
                currentTile + new Vector2i(-1, 1),
                currentTile + new Vector2i(0, -1),
                currentTile + new Vector2i(0, 1),
                currentTile + new Vector2i(1, -1),
                currentTile + new Vector2i(1, 0),
                currentTile + new Vector2i(1, 1),
            };

            foreach (var neighborTile in neighborTiles)
            {
                if (checkedTiles.Contains(neighborTile))
                    continue;

                checkedTiles.Add(neighborTile);

                var tileRef = _mapSys.GetTileRef(grid.Owner, grid, neighborTile);
                if (tileRef.Tile.IsEmpty)
                    continue;

                var neighborWorldPoint = grid.GridTileToWorldPos(neighborTile) + new Vector2(0.5f, 0.5f);
                var distance = (neighborWorldPoint - worldPoint).Length();
                var dist = (neighborWorldPoint - worldPoint).Normalized();

                // Более гибкая проверка направления
                var angleBetween = MathF.Acos(Vector2.Dot(dir, dist));
                var maxAngle = MathF.PI / 2f; // 90 градусов

                // Проверяем, находится ли центр тайла внутри полигона грида
                if (angleBetween <= maxAngle && distance <= MaxContactDistance * 2 && IsPointInPolygon(neighborWorldPoint, gridPolygon))
                {
                    contactArea.Add(neighborTile);
                    queue.Enqueue(neighborTile);
                }
            }
        }

        return contactArea;
    }

    /// <summary>
    /// Получает полигон, представляющий форму грида.
    /// </summary>
    private List<Vector2> GetGridPolygon(MapGridComponent grid)
    {
        // Получаем все непустые тайлы на гриде
        var nonEmptyTiles = new List<Vector2i>();
        foreach (var tile in grid.GetAllTiles())
        {
            if (!tile.Tile.IsEmpty)
            {
                nonEmptyTiles.Add(tile.GridIndices);
            }
        }

        // Если нет непустых тайлов, возвращаем пустой полигон
        if (nonEmptyTiles.Count == 0)
        {
            return new List<Vector2>();
        }

        // Преобразуем координаты тайлов в мировые координаты
        var worldPoints = nonEmptyTiles.Select(tile => grid.GridTileToWorldPos(tile) + new Vector2(0.5f, 0.5f)).ToList();

        // Используем алгоритм выпуклой оболочки для создания полигона
        return ConvexHull(worldPoints);
    }

    /// <summary>
    /// Проверяет, находится ли точка внутри полигона.
    /// </summary>
    private bool IsPointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        var windingNumber = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Count];

            if (p1.Y <= point.Y)
            {
                if (p2.Y > point.Y && IsLeft(p1, p2, point) > 0)
                {
                    windingNumber++;
                }
            }
            else
            {
                if (p2.Y <= point.Y && IsLeft(p1, p2, point) < 0)
                {
                    windingNumber--;
                }
            }
        }

        return windingNumber != 0;
    }

    /// <summary>
    /// Определяет, находится ли точка слева от вектора.
    /// </summary>
    private float IsLeft(Vector2 p1, Vector2 p2, Vector2 p)
    {
        return (p2.X - p1.X) * (p.Y - p1.Y) - (p.X - p1.X) * (p2.Y - p1.Y);
    }

    /// <summary>
    /// Алгоритм выпуклой оболочки Грэхема.
    /// </summary>
    private List<Vector2> ConvexHull(List<Vector2> points)
    {
        if (points.Count < 3)
        {
            return points;
        }

        // Находим самую нижнюю левую точку
        var startPoint = points.OrderBy(p => p.Y).ThenBy(p => p.X).First();

        // Сортируем точки по полярному углу относительно startPoint
        var sortedPoints = points.OrderBy(p =>
        {
            var angle = MathF.Atan2(p.Y - startPoint.Y, p.X - startPoint.X);
            return angle < 0 ? angle + 2 * MathF.PI : angle;
        }).ToList();

        // Инициализируем стек
        var stack = new Stack<Vector2>();
        stack.Push(startPoint);
        stack.Push(sortedPoints[0]);

        // Обходим все точки
        for (var i = 1; i < sortedPoints.Count; i++)
        {
            var top = stack.Pop();
            // Check if stack has at least 1 element before peeking.
            while (stack.Count > 0 && IsLeft(stack.Peek(), top, sortedPoints[i]) <= 0)
            {
                top = stack.Pop();
            }

            stack.Push(top);
            stack.Push(sortedPoints[i]);
        }

        return stack.ToList();
    }

    /// <summary>
    /// Находит ближайшую непустую плитку.
    /// </summary>
    private Vector2i FindNearestNonEmptyTile(MapGridComponent grid, Vector2i startTile)
    {
        var checkedTiles = new HashSet<Vector2i>();
        var queue = new Queue<Vector2i>();
        queue.Enqueue(startTile);
        checkedTiles.Add(startTile);

        while (queue.Count > 0)
        {
            var currentTile = queue.Dequeue();
            if (!_mapSys.GetTileRef(grid.Owner, grid, currentTile).Tile.IsEmpty)
            {
                return currentTile;
            }

            var neighborTiles = new[]
            {
                currentTile + new Vector2i(-1, -1),
                currentTile + new Vector2i(-1, 0),
                currentTile + new Vector2i(-1, 1),
                currentTile + new Vector2i(0, -1),
                currentTile + new Vector2i(0, 1),
                currentTile + new Vector2i(1, -1),
                currentTile + new Vector2i(1, 0),
                currentTile + new Vector2i(1, 1),
            };

            foreach (var neighborTile in neighborTiles)
            {
                if (checkedTiles.Contains(neighborTile))
                    continue;

                checkedTiles.Add(neighborTile);
                queue.Enqueue(neighborTile);
            }
        }
        return Vector2i.Zero;
    }
}

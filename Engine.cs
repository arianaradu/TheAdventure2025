using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();
    private readonly List<StaticGameObject> _houses = new();
    private readonly List<(int Id, DateTimeOffset ExpireTime)> _housesPendingRemoval = new(); // NEW

    private Level _currentLevel = new();
    private PlayerObject? _player;
    private DogCompanion? _dog;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;

        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
    }

    public void SetupWorld()
    {
        _player = new PlayerObject(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);
        _dog = new DogCompanion(_player, SpriteSheet.Load(_renderer, "Dog.json", "Assets"));

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null)
            {
                throw new Exception("Failed to load tile set");
            }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id!.Value, tile);
            }

            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null)
        {
            throw new Exception("Invalid level dimensions");
        }

        if (level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));

        _currentLevel = level;

        AddHouses();
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;

        _player?.UpdatePosition(up, down, left, right, 48, 48, msSinceLastFrame);
        _dog?.Update();
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        var playerPosition = _player!.Position;
        _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);

        RenderTerrain();
        RenderAllObjects();

        _renderer.PresentFrame();
    }

    private void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id);
        }

        // 🔥 Delayed house removal
        var now = DateTimeOffset.Now;
        var expiredHouses = _housesPendingRemoval.Where(h => now >= h.ExpireTime).ToList();
        foreach (var (id, _) in expiredHouses)
        {
            if (_gameObjects.TryGetValue(id, out var obj) && obj is StaticGameObject house)
            {
                _gameObjects.Remove(id);
                _houses.Remove(house);
            }
        }
        _housesPendingRemoval.RemoveAll(h => now >= h.ExpireTime);

        _dog?.Render(_renderer);
        _player?.Render(_renderer);
    }

    private void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            for (int i = 0; i < _currentLevel.Width; ++i)
            {
                for (int j = 0; j < _currentLevel.Height; ++j)
                {
                    int? dataIndex = j * currentLayer.Width + i;
                    if (dataIndex == null) continue;

                    var currentTileId = currentLayer.Data[dataIndex.Value] - 1;
                    if (currentTileId == null) continue;

                    var currentTile = _tileIdMap[currentTileId.Value];
                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    private IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    private void AddBomb(int screenX, int screenY)
    {
        var worldCoords = _renderer.ToWorldCoordinates(screenX, screenY);

        var bombSprite = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        bombSprite.ActivateAnimation("Explode");

        var bomb = new TemporaryGameObject(bombSprite, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);

        int explosionRadius = 80;

        var housesToRemove = _houses.Where(h =>
            RectsOverlap(worldCoords.X - explosionRadius / 2, worldCoords.Y - explosionRadius / 2,
                         explosionRadius, explosionRadius,
                         h.Position.X, h.Position.Y, h.Width, h.Height)).ToList();

        foreach (var house in housesToRemove)
        {
            var expireTime = DateTimeOffset.Now.AddSeconds(2.1); // delay until bomb ends
            _housesPendingRemoval.Add((house.Id, expireTime));
        }
    }

    private bool RectsOverlap(int x1, int y1, int w1, int h1, int x2, int y2, int w2, int h2)
    {
        return x1 < x2 + w2 &&
               x1 + w1 > x2 &&
               y1 < y2 + h2 &&
               y1 + h1 > y2;
    }

    private void AddHouses()
    {
        string[] houseFiles = {
            "house_2.png", "house_3.png", "house_4.png",
            "house_1.png", "house_5.png", "house_6.png"
        };

        int startX = 150;
        int startY = 300;
        int spacing = 10;

        int currentX = startX;
        foreach (string fileName in houseFiles)
        {
            string fullPath = Path.Combine("Assets", fileName);
            var textureId = _renderer.LoadTexture(fullPath, out var textureData);

            var house = new StaticGameObject(textureId, currentX, startY, textureData.Width, textureData.Height);
            _gameObjects.Add(house.Id, house);
            _houses.Add(house);

            currentX += textureData.Width + spacing;
        }
    }
}

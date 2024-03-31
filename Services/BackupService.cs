using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using Galdr;
using Newtonsoft.Json;
using Scum_Bag.DataAccess.Data;

namespace Scum_Bag.Services;

internal sealed class BackupService
{
    #region Fields

    private readonly Config _config;
    private readonly EventService _eventService;
    private readonly LoggingService _loggingService;
    private readonly Dictionary<Guid, Timer> _backupTimers = new();
    private readonly Dictionary<Guid, SaveGame> _saveGames = new();

    #endregion

    #region Constructor

    public BackupService(Config config, EventService eventService, LoggingService loggingService)
    {
        _config = config;
        _eventService = eventService;
        _loggingService = loggingService;

        Initialize();
    }

    #endregion

    #region Public Methods

    public void AddNewBackupTimer(SaveGame saveGame)
    {
        CreateInitialBackup(saveGame);
        _saveGames.Add(saveGame.Id, saveGame);
        AddTimer(saveGame);

        DisableDuplicates(saveGame.Id, saveGame.SaveLocation);
    }

    public void UpdateSave(SaveGame saveGame)
    {
        if (_saveGames.ContainsKey(saveGame.Id))
        {
            lock (_saveGames)
            {
                _saveGames[saveGame.Id] = saveGame;
            }
        }

        if (_backupTimers.ContainsKey(saveGame.Id))
        {
            _backupTimers[saveGame.Id].Enabled = false;
            _backupTimers[saveGame.Id].Dispose();
            _backupTimers.Remove(saveGame.Id);

            if (saveGame.Enabled)
            {
                AddTimer(saveGame);
            }
        }

        DisableDuplicates(saveGame.Id, saveGame.SaveLocation);
    }

    public void StopTimer(Guid id)
    {
        if (_backupTimers.ContainsKey(id))
        {
            _backupTimers[id].Enabled = false;
            _backupTimers[id].Dispose();
            _backupTimers.Remove(id);
        }
    }

    public bool CreateManualBackup(Guid id)
    {
        return CreateBackup(id);
    }

    #endregion

    #region Private Methods

    private void Initialize()
    {
        if (File.Exists(_config.SavesPath))
        {
            IEnumerable<SaveGame> saveGames = JsonConvert.DeserializeObject<IEnumerable<SaveGame>>(File.ReadAllText(_config.SavesPath));

            foreach(SaveGame saveGame in saveGames)
            {
                if (saveGame.Enabled && !_saveGames.ContainsKey(saveGame.Id))
                {
                    _saveGames.Add(saveGame.Id, saveGame);
                    AddTimer(saveGame);
                }
            }
        }
    }

    private void AddTimer(SaveGame saveGame)
    {
        Timer timer = new Timer(TimeSpan.FromMinutes(saveGame.Frequency));
        timer.Elapsed += (sender, e) => HandleTimer(saveGame.Id);
        timer.AutoReset = true;
        timer.Enabled = true;
        _backupTimers.Add(saveGame.Id, timer);
    }

    private void DisableDuplicates(Guid id, string location)
    {
        foreach (SaveGame saveGame in _saveGames.Values)
        {
            if (saveGame.Id != id && saveGame.SaveLocation == location)
            {
                saveGame.Enabled = false;

                if (_backupTimers.ContainsKey(saveGame.Id))
                {
                    _backupTimers[id].Enabled = false;
                    _backupTimers[id].Dispose();
                    _backupTimers.Remove(saveGame.Id);
                }
            }
        }
    }

    private string GetHash(string path)
    {
        string hash = String.Empty;
        byte[] hashData = null;

        using SHA256 mySHA256 = SHA256.Create();

        if (Directory.Exists(path))
        {
            List<byte> allHashes = new();

            DirectoryInfo dir = new(path);
            FileInfo[] files = dir.GetFiles("*.*", SearchOption.AllDirectories);

            foreach (FileInfo fileInfo in files)
            {
                if (fileInfo.Name != _config.BackupScreenshotName)
                {
                    try
                    {
                        using FileStream fileStream = fileInfo.Open(FileMode.Open);
                        fileStream.Position = 0;
                        byte[] hashValue = mySHA256.ComputeHash(fileStream);
                        allHashes.AddRange(hashValue);
                    }
                    catch (Exception e)
                    {
                        _loggingService.LogError($"Hash Error: {e}");
                    }
                }
            }

            hashData = mySHA256.ComputeHash(allHashes.ToArray());
        }
        else if (File.Exists(path))
        {
            FileInfo fileInfo = new(path);
            using FileStream fileStream = fileInfo.Open(FileMode.Open);

            try
            {
                fileStream.Position = 0;
                hashData = mySHA256.ComputeHash(fileStream);
            }
            catch { }
        }

        if (hashData != null)
        {
            StringBuilder builder = new();
            for (int i = 0; i < hashData.Length; i++)
            {
                builder.Append(hashData[i].ToString("x2"));
            }
            hash = builder.ToString();
        }

        return hash;
    }

    private void CreateInitialBackup(SaveGame saveGame)
    {
        string parentDirectory = Path.Combine(_config.DataDirectory, saveGame.Id.ToString());

        if (!Directory.Exists(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        string backupPath = Path.Combine(parentDirectory, DateTime.Now.Ticks.ToString());
        
        if (Directory.Exists(saveGame.SaveLocation))
        {
            CopyDirectory(saveGame.SaveLocation, backupPath);
        }
        else
        {
            Directory.CreateDirectory(backupPath);
            FileInfo fileInfo = new(saveGame.SaveLocation);
            string filePath = Path.Combine(backupPath, fileInfo.Name);
            File.Copy(saveGame.SaveLocation, filePath);
        }
    }

    private void CopyDirectory(string sourceDir, string destinationDir, bool recursive = true)
    {
        // Get information about the source directory
        DirectoryInfo dir = new(sourceDir);

        // Check if the source directory exists
        if (dir.Exists)
        {
            // Cache directories before we start copying
            DirectoryInfo[] dirs = dir.GetDirectories();

            // Create the destination directory
            Directory.CreateDirectory(destinationDir);

            // Get the files in the source directory and copy to the destination directory
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            // If recursive and copying subdirectories, recursively call this method
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }
    }

    private void HandleTimer(Guid id)
    {
        CreateBackup(id);
    }

    private bool CreateBackup(Guid id)
    {
        bool backedUp = false;

        if (_saveGames.ContainsKey(id))
        {
            SaveGame saveGame = _saveGames[id];
            string parentDirectory = Path.Combine(_config.DataDirectory, saveGame.Id.ToString());

            if (!Directory.Exists(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }
            
            DirectoryInfo parentDir = new(parentDirectory);
            DirectoryInfo newestDir = null;
            DirectoryInfo[] dirs = parentDir.GetDirectories();

            // find the latest backup
            foreach (DirectoryInfo dir in dirs)
            {
                if (newestDir == null || dir.CreationTime > newestDir.CreationTime)
                {
                    newestDir = dir;
                }
            }

            string lastBackupPath = newestDir.FullName;
            string lastBackupLocation = lastBackupPath;

            if (File.Exists(saveGame.SaveLocation))
            {
                FileInfo fileInfo = new(saveGame.SaveLocation);
                lastBackupLocation = Path.Combine(lastBackupPath, fileInfo.Name);
            }

            if (Directory.Exists(lastBackupPath))
            {
                string lastBackupHash = GetHash(lastBackupLocation);
                string currentHash = GetHash(saveGame.SaveLocation);

                if (lastBackupHash != currentHash)
                {
                    string backupPath = Path.Combine(parentDirectory, DateTime.Now.Ticks.ToString());

                    if (Directory.Exists(saveGame.SaveLocation))
                    {
                        CopyDirectory(saveGame.SaveLocation, backupPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(backupPath);
                        FileInfo fileInfo = new(saveGame.SaveLocation);
                        string filePath = Path.Combine(backupPath, fileInfo.Name);
                        File.Copy(saveGame.SaveLocation, filePath);
                    }

                    string screenShotPath = Path.Combine(parentDirectory, _config.LatestScreenshotName);

                    if (File.Exists(screenShotPath))
                    {
                        string newScreenshotPath = Path.Combine(backupPath, _config.BackupScreenshotName);
                        File.Copy(screenShotPath, newScreenshotPath, true);
                    }

                    Dictionary<string, BackupMetadata> metadata = GetSaveMetadata(id);

                    // actual count is dirs length + 1 because a new backup was just made,
                    // and don't count any favorites toward the max backups
                    int totalBackups = dirs.Length + 1 - metadata.Where(x => x.Value.IsFavorite).Count();

                    while (totalBackups > saveGame.MaxBackups)
                    {
                        DirectoryInfo oldestDir = null;

                        foreach (DirectoryInfo dir in dirs)
                        {
                            bool isFavorite = false;

                            if (metadata.TryGetValue(dir.FullName, out BackupMetadata backupMetadata))
                            {
                                isFavorite = backupMetadata.IsFavorite;
                            }

                            if (!isFavorite)
                            {
                                if (oldestDir == null || dir.CreationTime < oldestDir.CreationTime)
                                {
                                    oldestDir = dir;
                                }
                            }
                        }

                        if (oldestDir != null)
                        {
                            oldestDir.Delete(true);
                        }

                        totalBackups--;
                    }

                    backedUp = true;
                    _eventService.PublishEvent("saveUpdated", new { Id = id });
                }
            }
        }

        return backedUp;
    }

    private Dictionary<string, BackupMetadata> GetSaveMetadata(Guid id)
    {
        Dictionary<string, BackupMetadata> metadata = new();

        List<SaveGame> saveGames = JsonConvert.DeserializeObject<List<SaveGame>>(File.ReadAllText(_config.SavesPath));
            
        foreach (SaveGame saveGame in saveGames)
        {
            if (id == saveGame.Id)
            {
                metadata = saveGame.BackupMetadata;
                break;
            }
        }

        return metadata;
    }

    #endregion
}
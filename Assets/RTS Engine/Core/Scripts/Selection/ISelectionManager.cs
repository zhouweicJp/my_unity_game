﻿using System.Collections.Generic;

using RTSEngine.Entities;
using RTSEngine.Game;
using RTSEngine.UnitExtension;

namespace RTSEngine.Selection
{
    public interface ISelectionManager : IPreRunGameService
    {
        int Count { get; }
        int LocalFactionCount { get; }
        IUnitSquad SingleSelectedUnitSquad { get; }

        bool IsSelected(IEntity entity, bool localPlayerFaction = false);
        bool IsSelectedOnly(IEntity entity, bool localPlayerFaction = false);

        IEntity GetSingleSelectedEntity(EntityType type, bool localPlayerFaction = false);
        IDictionary<string, IEnumerable<IEntity>> GetEntitiesDictionary(EntityType type, bool localPlayerFaction);
        IEnumerable<IEntity> GetEntitiesList(EntityType type, bool exclusiveType, bool localPlayerFaction);

        bool Add(IEntity entity, SelectionType type, bool isLocalPlayerClickSelection);
        bool Add(IEnumerable<IEntity> entities);

        bool Remove(IEntity entity);
        void Remove(IEnumerable<IEntity> entities);
        void RemoveAll();
        bool IsUnitSquadSelectedOnly();
        bool IsUnitSquadSelectedOnly(IUnitSquad unitSquad);
    }
}
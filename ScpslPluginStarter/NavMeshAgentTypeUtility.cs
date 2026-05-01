using System;
using UnityEngine.AI;

namespace ScpslPluginStarter;

internal static class NavMeshAgentTypeUtility
{
    public static int DefaultAgentTypeId
    {
        get
        {
            try
            {
                return NavMesh.GetSettingsCount() > 0
                    ? NavMesh.GetSettingsByIndex(0).agentTypeID
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public static NavMeshBuildSettings CreateDefaultBuildSettings()
    {
        int agentTypeId = DefaultAgentTypeId;
        try
        {
            return NavMesh.GetSettingsByID(agentTypeId);
        }
        catch (Exception)
        {
            NavMeshBuildSettings settings = NavMesh.CreateSettings();
            settings.agentTypeID = agentTypeId;
            return settings;
        }
    }
}

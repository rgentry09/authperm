﻿// Copyright (c) 2021 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AuthPermissions.BulkLoadServices.Concrete.Internal;
using AuthPermissions.CommonCode;
using AuthPermissions.DataLayer.Classes;
using AuthPermissions.DataLayer.EfCode;
using AuthPermissions.SetupCode;
using Microsoft.EntityFrameworkCore;
using StatusGeneric;

namespace AuthPermissions.BulkLoadServices.Concrete
{
    /// <summary>
    /// This allows you to bulk load users, with their Roles and (optional) Tenant
    /// </summary>
    public class BulkLoadUsersService : IBulkLoadUsersService
    {
        private readonly AuthPermissionsDbContext _context;
        private readonly IFindUserInfoService _findUserInfoService;
        private readonly IAuthPermissionsOptions _options;

        public BulkLoadUsersService(AuthPermissionsDbContext context, IFindUserInfoService findUserInfoService, IAuthPermissionsOptions options)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _findUserInfoService = findUserInfoService;
            _options = options;
        }

        /// <summary>
        /// This allows you to add a series of users with their roles and the tenant (if <see cref="IAuthPermissionsOptions.TenantType"/> says tenants are used
        /// </summary>
        /// <param name="userDefinitions">A list of <see cref="DefineUserWithRolesTenant"/> containing the information on users and what auth roles they have.
        /// In this case the UserId must be filled in with the authorized users' UserId, or the <see cref="IFindUserInfoService"/> can find a user's ID
        /// </param>
        /// <returns>A status so that errors can be returned</returns>
        public async Task<IStatusGeneric> AddUsersRolesToDatabaseAsync(List<DefineUserWithRolesTenant> userDefinitions)
        {
            var status = new StatusGenericHandler();

            if (userDefinitions == null || !userDefinitions.Any())
                return status;

            for (int i = 0; i < userDefinitions.Count; i++)
            {
                status.CombineStatuses(await CreateUserTenantAndAddToDbAsync(userDefinitions[i], i));
            }

            if (status.IsValid)
                status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            status.Message = $"Added {userDefinitions.Count} new users with associated data to the auth database";
            return status;
        }

        //------------------------------------------
        //private methods

        private async Task<IStatusGeneric> CreateUserTenantAndAddToDbAsync(DefineUserWithRolesTenant userDefine, int index)
        {
            var status = new StatusGenericHandler();

            var rolesToPermissions = new List<RoleToPermissions>();
            userDefine.RoleNamesCommaDelimited.DecodeCodeNameWithTrimming(0, 
                (name, startOfName) => 
                {
                    var roleToPermission = _context.RoleToPermissions.SingleOrDefault(x => x.RoleName == name);
                    if (roleToPermission == null)
                        status.AddError(userDefine.RoleNamesCommaDelimited.FormErrorString(index, startOfName,
                            $"The role {name} wasn't found in the auth database."));
                    else
                        rolesToPermissions.Add(roleToPermission);
                });

            if (!rolesToPermissions.Any())
                status.AddError(userDefine.RoleNamesCommaDelimited.FormErrorString(index-1, -1,
                    $"The user {userDefine.UserName} didn't have any roles."));

            if (status.HasErrors)
                return status;

            var userId = userDefine.UserId;
            var userName = userDefine.UserName;
            if (userId == null && _findUserInfoService != null)
            {
                var userInfo = await _findUserInfoService.FindUserInfoAsync(userDefine.UniqueUserName);
                userId =  userInfo.UserId;
                if (userInfo.UserName != null)
                    //we override the AuthUser username
                    userName = userInfo.UserName;
            }
            if (userId == null)
                return status.AddError(userDefine.UniqueUserName.FormErrorString(index - 1, -1,
                    $"The user {userName} didn't have a userId and the {nameof(IFindUserInfoService)}" +
                    (_findUserInfoService == null ? " wasn't available." : " couldn't find it either.")));

            Tenant userTenant = null;
            if (_options.TenantType != TenantTypes.NotUsingTenants)
            {
                if(string.IsNullOrEmpty(userDefine.TenantNameForDataKey))
                    return status.AddError(userDefine.UniqueUserName.FormErrorString(index - 1, -1,
                        $"You have defined this is a multi-tenant application, but user {userName} has no tenant name defined in the {nameof(userDefine.TenantNameForDataKey)}."));


                userTenant = await _context.Tenants.SingleOrDefaultAsync(x => x.TenantName == userDefine.TenantNameForDataKey);
                if (userTenant == null)
                    return status.AddError(userDefine.UniqueUserName.FormErrorString(index - 1, -1,
                        $"The user {userName} has a tenant name of {userDefine.TenantNameForDataKey} which wasn't found in the auth database."));
            }

            var authUser = new AuthUser(userId, userDefine.Email, userName, rolesToPermissions, userTenant);
            _context.Add(authUser);

            return status;
        }
    }
}
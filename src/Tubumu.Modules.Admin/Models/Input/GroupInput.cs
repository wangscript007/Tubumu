﻿using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Tubumu.Modules.Framework.Models;

namespace Tubumu.Modules.Admin.Models.Input
{
    /// <summary>
    /// 分组 Id Input
    /// </summary>
    public class GroupIdInput
    {
        [Required(ErrorMessage = "请输入分组Id")]
        public Guid GroupId { get; set; }
    }

    /// <summary>
    /// 分组 Input
    /// </summary>
    public class GroupInput
    {
        /// <summary>
        /// 分组 Id
        /// </summary>
        [DisplayName("分组 Id")]
        public Guid? GroupId { get; set; }

        /// <summary>
        /// 所属分组（父分组） Id
        /// </summary>
        [DisplayName("所属分组")]
        public Guid? ParentId { get; set; }

        /// <summary>
        /// 分组名称
        /// </summary>
        [Required(ErrorMessage = "分组名称不能为空")]
        [StringLength(50, ErrorMessage = "分组名称请保持在50个字符以内")]
        //[SlugWithChinese(ErrorMessage = "分组名称只能包含中文、字母、数字、_和-")]
        [DisplayName("分组名称")]
        public string Name { get; set; }

        /// <summary>
        /// 是否停用
        /// </summary>
        public bool IsDisabled { get; set; }

        /// <summary>
        /// 是否允许包含用户
        /// </summary>
        public bool IsContainsUser { get; set; }

        /// <summary>
        /// 包含角色 Id
        /// </summary>
        public Guid[] RoleIds { get; set; }

        /// <summary>
        /// 分组可用的角色(该分组的用户的直接角色只能使用 AvailableRoles)
        /// </summary>
        public Guid[] AvailableRoleIds { get; set; }

        /// <summary>
        /// 分组拥有的权限
        /// </summary>
        public Guid[] PermissionIds { get; set; }
    }

    /// <summary>
    /// 移动分组 Input
    /// </summary>
    public class MoveGroupInput
    {
        /// <summary>
        /// 源分组 Id
        /// </summary>
        public Guid SourceId { get; set; }

        /// <summary>
        /// 目标分组 Id
        /// </summary>
        public Guid TargetId { get; set; }

        /// <summary>
        /// 移动位置
        /// </summary>
        public MovingLocation MovingLocation { get; set; }

        /// <summary>
        /// 是否作为子节点
        /// </summary>
        public bool? IsChild { get; set; }
    }
}
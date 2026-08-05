using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NLog;
using Sandbox.Game.Entities.Cube;
using Torch.API.Plugins;
using Torch.API;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Torch;

namespace WheelControlFix
{
    /// <summary>
    /// Torch plugin that fixes the Space Engineers wheel control bug.
    /// 
    /// Bug: Once a player enters a cockpit, wheel direction settings (m_wheelInversions) are calculated
    /// based on cockpit-to-wheel spatial relationship and persist after exit. The Accelerate() method
    /// always uses m_wheelInversions.RevolveInvert when it should only use it when PropulsionOverride == 0.
    /// 
    /// Fix: Transpile Accelerate() to restore the old conditional logic.
    /// </summary>
    public class WheelControlFix : TorchPluginBase
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _log.Info("WheelControlFix: Initializing...");
        }
    }

    /// <summary>
    /// Torch patch shim that applies MSIL patches to fix the wheel control bug.
    /// 
    /// This class is auto-discovered by Torch's PatchManager via the [PatchShim] attribute.
    /// It patches MyMotorSuspension.Accelerate() and ReleaseControl() methods.
    /// </summary>
    [PatchShim]
    internal static class WheelControlPatch
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        // Cache for reflection
        private static MethodInfo _propulsionOverrideGetter;
        private static MethodBase _accelerateMethod;
        private static MethodBase _releaseControlMethod;

        public static void Patch(PatchContext ctx)
        {
            var myMotorSuspensionType = typeof(MyMotorSuspension);

            // Get PropulsionOverride property getter
            var propProp = myMotorSuspensionType.GetProperty("PropulsionOverride",
                BindingFlags.Public | BindingFlags.Instance);
            _propulsionOverrideGetter = propProp?.GetGetMethod();

            if (_propulsionOverrideGetter == null)
            {
                _log.Error("WheelControlPatch: PropulsionOverride getter not found!");
                return;
            }

            // Get Accelerate method (private)
            _accelerateMethod = myMotorSuspensionType.GetMethod("Accelerate",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (_accelerateMethod == null)
            {
                _log.Error("WheelControlPatch: Accelerate method not found!");
                return;
            }

            // Get ReleaseControl method (private)
            _releaseControlMethod = myMotorSuspensionType.GetMethod("ReleaseControl",
                BindingFlags.NonPublic | BindingFlags.Instance);

            _log.Info("WheelControlPatch: Reflection setup complete.");

            // Patch Accelerate with transpiler
            var acceleratePattern = ctx.GetPattern(_accelerateMethod);
            acceleratePattern.Transpilers.Add(typeof(WheelControlPatch).GetMethod(nameof(TranspileAccelerate), BindingFlags.NonPublic | BindingFlags.Static));
            _log.Info("WheelControlPatch: Added transpiler for Accelerate method.");

            // Patch ReleaseControl with suffix (if method exists)
            if (_releaseControlMethod != null)
            {
                var releaseControlPattern = ctx.GetPattern(_releaseControlMethod);
                releaseControlPattern.Suffixes.Add(typeof(WheelControlPatch).GetMethod(nameof(SuffixReleaseControl), BindingFlags.NonPublic | BindingFlags.Static));
                _log.Info("WheelControlPatch: Added suffix for ReleaseControl method.");
            }
        }

        /// <summary>
        /// Transpiler for MyMotorSuspension.Accelerate() that restores the old conditional logic.
        /// </summary>
        private static IEnumerable<MsilInstruction> TranspileAccelerate(IEnumerable<MsilInstruction> instructions)
        {
            var msil = instructions.ToList();
            var result = new List<MsilInstruction>();

            // Find indices for the pattern we need to modify
            // Pattern: ldarg.0, ldflda m_wheelInversions, ldfld RevolveInvert, ldarg.2, ceq
            // The ceq is unique, so find it first, then derive ldarg.0 index (4 instructions before)
            int ceqIdx = msil.FindIndex(i => i.OpCode == OpCodes.Ceq);
            int ldarg0Idx = ceqIdx - 4; // ldarg.0 is at index [ceqIdx - 4]

            if (ldarg0Idx < 0 || ceqIdx < 0)
            {
                _log.Error($"WheelControlPatch: Pattern not found. ldarg.0={ldarg0Idx}, ceq={ceqIdx}");
                return msil;
            }

            // Validate the pattern matches expected sequence
            if (ldarg0Idx + 4 >= msil.Count ||
                msil[ldarg0Idx].OpCode != OpCodes.Ldarg_0 ||
                msil[ldarg0Idx + 1].OpCode != OpCodes.Ldflda ||
                msil[ldarg0Idx + 2].OpCode != OpCodes.Ldfld ||
                msil[ldarg0Idx + 3].OpCode != OpCodes.Ldarg_2)
            {
                _log.Error($"WheelControlPatch: Pattern mismatch at indices ldarg.0={ldarg0Idx}. Expected ldarg.0/ldflda/ldfld/ldarg.2/ceq.");
                return msil;
            }

            _log.Info($"WheelControlPatch: Found pattern at indices ldarg.0={ldarg0Idx}, ceq={ceqIdx}");

            // Create labels for branching
            var labelUseForward = new MsilLabel();    // Jump here if PropulsionOverride != 0
            var labelEnd = new MsilLabel();           // End of comparison

            // ============================================
            // COPY: All instructions BEFORE the pattern (early-return guards)
            // This ensures we don't crash on uninitialized fields.
            // The original code has early returns for IsWorking, TopGrid, Physics.
            // We must insert our check AFTER these guards so m_propulsionOverride
            // is guaranteed to be initialized.
            // ============================================
            for (int i = 0; i < ldarg0Idx; i++)
            {
                result.Add(msil[i]);
            }

            // ============================================
            // INSERTED: PropulsionOverride != 0 check
            // If PropulsionOverride != 0, load 'forward' and skip the inversion comparison
            // Placed right before m_wheelInversions access to ensure safe initialization.
            // ============================================
            result.Add(new MsilInstruction(OpCodes.Ldarg_0)); // load 'this'
            result.Add(new MsilInstruction(OpCodes.Callvirt));
            result.Last().InlineValue(_propulsionOverrideGetter);
            result.Add(new MsilInstruction(OpCodes.Ldc_R4).InlineValue(0.0f)); // load 0.0f
            result.Add(new MsilInstruction(OpCodes.Ceq)); // PropulsionOverride == 0?
            result.Add(new MsilInstruction(OpCodes.Brfalse_S).InlineTarget(labelUseForward));

            // ============================================
            // ORIGINAL CODE (when PropulsionOverride == 0)
            // ============================================
            // Copy from ldarg.0 through ceq (the original comparison)
            for (int i = ldarg0Idx; i <= ceqIdx; i++)
            {
                result.Add(msil[i]);
            }

            // Jump past the forward path
            result.Add(new MsilInstruction(OpCodes.Br_S).InlineTarget(labelEnd));

            // ============================================
            // FORWARD PATH (when PropulsionOverride != 0)
            // ============================================
            var ldarg2Inst = new MsilInstruction(OpCodes.Ldarg_2); // load 'forward' parameter
            ldarg2Inst.Labels.Add(labelUseForward);
            result.Add(ldarg2Inst);

            // ============================================
            // END MARKER (continuation point)
            // ============================================
            result.Add(new MsilInstruction(OpCodes.Nop).LabelWith(labelEnd));

            // ============================================
            // REMAINING INSTRUCTIONS (stloc.s 5 and after)
            // ============================================
            for (int i = ceqIdx + 1; i < msil.Count; i++)
            {
                result.Add(msil[i]);
            }

            _log.Info($"WheelControlPatch: Transpiled Accelerate ({msil.Count} -> {result.Count} instructions)");
            return result;
        }

        /// <summary>
        /// Suffix for ReleaseControl to reset m_wheelInversions to default values.
        /// This ensures that wheel inversions don't persist after a player exits a cockpit.
        /// </summary>
        private static void SuffixReleaseControl(MyMotorSuspension instance)
        {
            if (instance == null) return;

            try
            {
                var field = typeof(MyMotorSuspension)
                    .GetField("m_wheelInversions", BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                {
                    // Create default instance of the wheel inversions struct
                    var defaults = Activator.CreateInstance(field.FieldType);
                    field.SetValue(instance, defaults);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"WheelControlPatch: SuffixReleaseControl error: {ex.Message}");
            }
        }
    }
}

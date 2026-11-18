using Elements;
using Elements.Battle;
using Newtonsoft.Json;
using Org.BouncyCastle.Utilities;
using PCRCaculator;
using PCRCaculator.Battle;
using PCRCaculator.Guild;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;


#if ENABLE_THREADING
public class ThreadController : IDisposable
{
    private readonly PCRCalculatorServer.IdleWorkerPool<(BattleSimulationRequest request, Action callback, Action<Exception> callbackFailed)> pool;

    public ThreadController(int? workerNum = null, int idleReleaseSeconds = 300)
    {
        int effectiveWorkerNum = workerNum ?? ProcessorHelper.GetTotalLogicalProcessorCount();
        
        Console.WriteLine($"[ThreadController] 检测到处理器组数: {ProcessorHelper.GetProcessorGroupCount()}, 总逻辑处理器数: {ProcessorHelper.GetTotalLogicalProcessorCount()}, 使用线程数: {effectiveWorkerNum}");
        
        new SpineCreator().Awake();
        new BaseBackManager().Awake();
        new MainManager().Awake();
        PCRCalculatorServer.ServerOptions.ApplyClientContext();

        new BattleUIManager()
        {
            parent = new(),
            timeScaleSlider = new(),
            timeText = new(),
        }.Awake();

        MainManager.Instance.Start();
        while (!MainManager.Instance.LoadFinished)
            Thread.Sleep(0);

        pool = new(effectiveWorkerNum, TimeSpan.FromSeconds(idleReleaseSeconds), index =>
        {
            ProcessorHelper.SetThreadGroupAffinityForIndex(index);
            ThreadInit();
        }, work =>
        {
            Simulate(work.request);
            work.callback();
        }, (work, error) =>
        {
            if (work.callbackFailed != null) work.callbackFailed(error);
            else Console.WriteLine(error);
        }, PCRCalculatorServer.ServerBattleResources.Release);
    }

    public void DispatchTask(BattleSimulationRequest request, Action callback, Action<Exception> callbackFailed)
    {
        pool.Dispatch((request, callback, callbackFailed));
    }

    internal PCRCalculatorServer.IdleWorkerPoolSnapshot ResourceState => pool.Snapshot;
    private static void ThreadInit()
    {
        new GuildManager()
        {
            calSlider = new()
        }.Awake();

        GuildManager.Instance.SettingData.start_hp = "1,1,1,1,1";
        GuildManager.Instance.SettingData.start_tp = "0,0,0,0,0";

        new TalentLevelManager().Awake();
        new TalentSkillTreeManager().Awake();
        new TeamSkillTreeManager().Awake();
        new RoleMasteryManager().Awake();

        const float parentScale = 1 / 540f;
        const float parentOffset = 5000f * parentScale;
        new MyGameCtrl()
        {
            stopFrame = -1, // Headless simulations have no interactive pause frame.
            unitParent = new()
            {
                localScale = parentScale * Vector3.one,
                localPosition = new Vector3(0, parentOffset, 0)
            },
            enemyParent = new()
            {
                localScale = parentScale * Vector3.one,
                localPosition = new Vector3(0, parentOffset, 0)
            },
            firearmPrefab = new()
        }.Awake();
        new BattleHeaderController().Awake();
        new GuildCalculator().Awake();

        new BattleManager()
        {
            gameObject = new(),
            playCamera = new(),
            bgTransform = new()
        }.Awake();
        MyGameCtrl.Instance.playerUnitCtrl = new List<UnitCtrl>();
        MyGameCtrl.Instance.enemyUnitCtrl = new List<UnitCtrl>();
        MyGameCtrl.Instance.extraEffectEnemyUnitCtrl = new List<UnitCtrl>();

        MyGameCtrl.Instance.Start();

        CoroutineRunner.Initialize();
    }

    [ThreadStatic] private static Stopwatch watch;

    private static void Simulate(BattleSimulationRequest request)
    {
        try
        {
            BattleManager.Instance.enabled = false;
            BattleManager.Instance.battleFinished = false;
            BattleManager.Instance.battleFinishedProcessed = false;

            MyGameCtrl.Instance.StartByServer(request);

            // MyGameCtrl.Instance.tempData.skipping = request.IsSkipping;

            IEnumerator UpdateBattleManager()
            {
                for (;;)
                {
                    if (BattleManager.Instance.enabled)
                        BattleManager.Instance.Update();
                    yield return null;
                }
            }

            CoroutineRunner.AppendCoroutine(UpdateBattleManager());
            
            watch ??= new Stopwatch();
            watch.Restart();
            
            while (!BattleManager.Instance.battleFinishedProcessed)
            {
                CoroutineRunner.Update();
                if (watch.ElapsedMilliseconds > 60000)
                {
                    Console.WriteLine("battle stuck detected, request = " + JsonConvert.SerializeObject(request));
                    throw new Exception();
                }
            }
        }
        finally
        {
            if (!MyGameCtrl.Instance.isBattleFinish)
            {
                BattleManager.Instance.DoCleaning(1);
            }

            UnityEngine.CoroutineRunner.pending.Clear();
            UnityEngine.CoroutineRunner.running.Clear();

        }

    }

    public void Dispose()
    {
        pool.Dispose();
    }
}

#endif

import window from '@ohos.window';
import { AbilityConstant, UIAbility, Want } from '@kit.AbilityKit';
import { SetToGlobalThis, TuanjiePlayer, TuanjieLog } from 'tuanjieLib';
//import { launchAcceleration } from '@kit.GraphicsAccelerateKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { Environment } from '@kit.CoreFileKit';
import { abilityAccessCtrl, Permissions } from '@kit.AbilityKit';

export default class TuanjiePlayerAbility extends UIAbility {
  private defaultPageUri: string = "pages/Index"
  private tuanjiePlayer : TuanjiePlayer = TuanjiePlayer.getInstance()
  private mainWindow: window.Window | null = null
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    this.setConfig();
    this.registerHmsAbility();
    // Request permissions first, then initialize Unity based on the result
    this.requestPermissions(want, launchParam);
  }

  onDestroy(): void {
    this.tuanjiePlayer.onDestroy();
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    windowStage.loadContent(this.defaultPageUri, (err: ESObject, data: ESObject) => {
        if (err.code) {
          TuanjieLog.error('Failed to load the content. Cause: %{public}s', JSON.stringify(err) ?? '');
          return;
        }
        TuanjieLog.info('Succeeded in loading the content. Data: %{public}s', JSON.stringify(data) ?? '');
      });
    
    // Get and store the main window for later use
    windowStage.getMainWindow((err, window) => {
      if (err.code) {
        TuanjieLog.error('Failed to get main window. Cause: %{public}s', JSON.stringify(err) ?? '');
        return;
      }
      this.mainWindow = window;
      this.setFullScreen();
    });
    
    this.tuanjiePlayer.onWindowStageCreate(windowStage)
  }

  onWindowStageWillDestroy(): void {
    this.tuanjiePlayer.onWindowStageWillDestroy();
  }
  
  onWindowStageDestroy(): void {
    this.tuanjiePlayer.onWindowStageDestroy()
  }

  onForeground(): void {
    // Re-apply fullscreen when app comes back to foreground (e.g., after permission dialog)
    this.setFullScreen();
    this.tuanjiePlayer.onForeground()
  }

  onBackground(): void {
    this.tuanjiePlayer.onBackground()
  }

  onNewWant(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    this.tuanjiePlayer.onNewWant(want,launchParam)
  }

  setConfig(): void {
    SetToGlobalThis("staticSplashScreenFit", $r('app.integer.StaticSplashScreenFit'));
    SetToGlobalThis("appSplash", $r('app.media.app_splash'));
    SetToGlobalThis("showStaticSplash",$r('app.integer.ShowStaticSplashScreen'));
  }

  private registerHmsAbility() {
    SetToGlobalThis("isLaunchSnapshotEnabled", this.isLaunchSnapshotEnabled);
  }

  private isLaunchSnapshotEnabled(): boolean {
    let enable = false;
    if (canIUse("SystemCapability.GraphicsGame.LaunchAcceleration")) {
      //enable = launchAcceleration.isLaunchMirrorEnabled();
      TuanjieLog.info(`launchAcceleration isLaunchMirrorEnabled enable = ${enable}`);
    }
    return enable;
  }

  private requestPermissions(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    const permissions: Array<Permissions> = ['ohos.permission.READ_WRITE_DOCUMENTS_DIRECTORY'];
    let atManager = abilityAccessCtrl.createAtManager();
    
    atManager.requestPermissionsFromUser(this.context, permissions).then((data) => {
      let grantStatus: Array<number> = data.authResults;
      let allGranted = true;
      
      for (let i = 0; i < grantStatus.length; i++) {
        if (grantStatus[i] === 0) {
          // Permission granted
          TuanjieLog.info(`Permission ${permissions[i]} granted`);
        } else {
          // Permission denied
          TuanjieLog.error(`Permission ${permissions[i]} denied`);
          allGranted = false;
        }
      }
      
      if (allGranted) {
        // All permissions granted, initialize Unity
        TuanjieLog.info('All permissions granted, initializing TuanjiePlayer');
        this.tuanjiePlayer.onCreate(this.context, want, launchParam);
        // Re-apply fullscreen after permission dialog
        this.setFullScreen();
      } else {
        // Permission denied, terminate the ability
        TuanjieLog.error('Required permissions denied, terminating ability');
        this.context.terminateSelf();
      }
    }).catch((err: BusinessError) => {
      TuanjieLog.error(`Failed to request permissions. Code: ${err.code}, message: ${err.message}`);
      // Permission request failed, terminate the ability
      this.context.terminateSelf();
    });
  }

  private setFullScreen(): void {
    if (this.mainWindow) {
      this.mainWindow.setWindowLayoutFullScreen(true, (err) => {
        if (err.code) {
          TuanjieLog.error('Failed to set fullscreen. Cause: %{public}s', JSON.stringify(err) ?? '');
          return;
        }
        TuanjieLog.info('Succeeded in setting fullscreen');
      });
    }
  }
}

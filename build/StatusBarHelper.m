#import <Cocoa/Cocoa.h>

// Menu bar helper for Sheas Cealer Nix
// Runs as a separate process, creates an NSStatusItem with a menu.
// Communicates with the C# parent via stdin/stdout:
//   ObjC → C# (stdout): "click:<menu title>\n"
//   C# → ObjC (stdin):  "quit\n" (terminate helper)

@interface MenuTarget : NSObject
@end

@implementation MenuTarget
- (void)menuClicked:(NSMenuItem *)sender {
    printf("click:%s\n", [[sender title] UTF8String]);
    fflush(stdout);
}
@end

@interface AppDelegate : NSObject <NSApplicationDelegate>
@property (strong) NSStatusItem *statusItem;
@property (strong) MenuTarget *menuTarget;
@end

@implementation AppDelegate

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    self.statusItem = [NSStatusBar.systemStatusBar statusItemWithLength:-1.0]; // NSVariableStatusItemLength
    self.statusItem.button.title = @"C";

    self.menuTarget = [[MenuTarget alloc] init];

    NSMenu *menu = [[NSMenu alloc] init];
    NSArray *entries = @[
        @"显示窗口",
        @"separator",
        @"启动伪造",
        @"启用全局伪造",
        @"停止全局伪造",
        @"separator",
        @"更新上游规则",
        @"退出"
    ];

    for (NSString *entry in entries) {
        if ([entry isEqualToString:@"separator"]) {
            [menu addItem:[NSMenuItem separatorItem]];
        } else {
            NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:entry
                                                          action:@selector(menuClicked:)
                                                   keyEquivalent:@""];
            item.target = self.menuTarget;
            [menu addItem:item];
        }
    }

    self.statusItem.menu = menu;

    // Read stdin on background thread; terminate on "quit" or EOF
    dispatch_async(dispatch_get_global_queue(0, 0), ^{
        char buf[256];
        while (fgets(buf, sizeof(buf), stdin)) {
            NSString *line = [NSString stringWithUTF8String:buf];
            if ([[line stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet]
                 isEqualToString:@"quit"]) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    [NSApp terminate:nil];
                });
                return;
            }
        }
        // stdin closed (parent exited)
        dispatch_async(dispatch_get_main_queue(), ^{
            [NSApp terminate:nil];
        });
    });
}

@end

int main(int argc, const char *argv[]) {
    NSApplication *app = NSApplication.sharedApplication;
    AppDelegate *delegate = [[AppDelegate alloc] init];
    app.delegate = delegate;
    [app setActivationPolicy:NSApplicationActivationPolicyAccessory];
    [app run];
    return 0;
}
